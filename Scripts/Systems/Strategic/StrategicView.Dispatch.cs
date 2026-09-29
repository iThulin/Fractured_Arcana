using Godot;
using System.Collections.Generic;

// ============================================================
// StrategicView.Dispatch.cs: the command column, the clock, focus (2026-09-28/29)
//
// Purpose:        Reported by Magos: selecting a force was hard, and once
//                 selected, hitting a waystone was harder. Ruled: build the
//                 rail AND fix the map, with the map staying the place the
//                 plan is made.
//
//                 The rail sits under the Forces panel, on the left, where
//                 nothing else docks. For the selected force it lists every
//                 place it can go with the method and the cost, and the one
//                 verb for where it stands (Take the field / Sortie). Hovering
//                 a row lights its ring on the map; pressing it swings the
//                 camera there (same zoom) and opens the order. Two clicks
//                 from "which force" to "gone", however far out the camera is.
//
//                 The map side: a click on a piece's body or name selects it
//                 (WorldAtlas3D.PiecePicked); with Move armed, a click on
//                 another piece means "go to where it stands". A click within
//                 DispatchTargets.SnapTiles of a waystone means the waystone,
//                 with a "walk to the tile instead" escape on the confirm, so
//                 the snap never eats a real walking order.
//
//                 A step-through carries "and take the field": the two orders
//                 that used to be two trips to the chrome are one.
// Layer:          UI (StrategicView partial)
// Collaborators:  DispatchTargets (the list), WorldAtlas3D.Dispatch (rings,
//                 picking), FieldMarch (Teleport), TryMovePartyTo,
//                 TryMarchTo, OnPartyTakeTheField, OnSortiePressed
// ============================================================

public partial class StrategicView
{
    // ── The command column (2026-09-29) ──────────────────────────────────
    //    Ruled (Magos, "Command column"): the left side is ONE column of cards
    //    that each fit their content, top to bottom:
    //      View      the map lens
    //      Forces    the nav toggle and the roster
    //      Orders    the selected force: its name, its state, every verb it
    //                has (moved here from the right-hand stack, so a force's
    //                orders sit under its name instead of across the screen)
    //      Destinations  where it can go, and what that costs
    //    The right side keeps only what belongs to no force: the Forces
    //    screen, help, the city's own verbs, and the news. One card style, one
    //    padding, one gap, everywhere.

    /// <summary>Width of the left column, cards included.</summary>
    private const int CommandWidth = 320;
    private const int CardGap = 8;
    private const int CardPadX = 12;
    private const int CardPadY = 10;

    private VBoxContainer _commandColumn;
    private PanelContainer _ordersCard;
    private Label _ordersTitle;
    private Label _ordersStatus;
    private Label _ordersBlocked;
    private VBoxContainer _ordersBox;
    private Label _ordersNone;

    private PanelContainer _dispatchPanel;
    private Label _dispatchTitle;
    private Button _dispatchCollapseBtn;
    private ScrollContainer _dispatchScroll;
    private VBoxContainer _dispatchRows;
    private bool _dispatchCollapsed;

    /// <summary>Tallest the destinations list grows before it scrolls.</summary>
    private const int DestinationsMaxHeight = 340;
    private const int DestinationRowHeight = 46;

    /// <summary>The one card style: raised slate, the violet rule every panel
    /// on this screen shares.</summary>
    private static StyleBox CardStyle()
        => UITheme.MakePanelStyle(UITheme.BgRaised, UITheme.CampusTitleBarBorder);

    /// <summary>A card: panel, padding, and a VBox to fill. Returned both so
    /// the caller can keep the panel (to hide it) and fill the box.</summary>
    private static (PanelContainer card, VBoxContainer body) MakeCard()
    {
        var card = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop };
        card.AddThemeStyleboxOverride("panel", CardStyle());
        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", CardPadX);
        margin.AddThemeConstantOverride("margin_right", CardPadX);
        margin.AddThemeConstantOverride("margin_top", CardPadY);
        margin.AddThemeConstantOverride("margin_bottom", CardPadY);
        card.AddChild(margin);
        var body = new VBoxContainer();
        body.AddThemeConstantOverride("separation", 6);
        margin.AddChild(body);
        return (card, body);
    }

    /// <summary>A card's small uppercase heading.</summary>
    private static Label MakeCardHeading(string text)
    {
        var l = new Label { Text = text, ClipText = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        l.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
        l.AddThemeColorOverride("font_color", UITheme.TextDim);
        return l;
    }

    /// <summary>The column itself: a transparent stack, so the map between and
    /// below the cards still takes clicks. Built on first use by whichever card
    /// asks first (the lens row, in practice).</summary>
    private void EnsureCommandColumn()
    {
        if (_commandColumn != null || _hud == null)
        {
            return;
        }
        _commandColumn = new VBoxContainer
        {
            AnchorLeft = 0f,
            AnchorTop = 0f,
            AnchorRight = 0f,
            AnchorBottom = 1f,
            OffsetLeft = 16,
            OffsetRight = 16 + CommandWidth,
            OffsetTop = 8 + HudManager.BarHeight,
            OffsetBottom = -16,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _commandColumn.AddThemeConstantOverride("separation", CardGap);
        _hud.AddChild(_commandColumn);
    }

    /// <summary>Build the Orders and Destinations cards and move the force
    /// verbs into Orders. Called from BuildForcesPanel, after the Forces card
    /// joins the column, and after BuildHud has made every verb.</summary>
    private void BuildDispatchRail()
    {
        if (_hud == null || _dispatchPanel != null)
        {
            return;
        }
        EnsureCommandColumn();

        // Orders.
        var (orders, ob) = MakeCard();
        _ordersCard = orders;
        _ordersCard.Visible = false;   // RefreshDispatch decides, once there is a selection to show
        _commandColumn.AddChild(_ordersCard);
        _ordersTitle = new Label { ClipText = true };
        _ordersTitle.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize);
        _ordersTitle.AddThemeColorOverride("font_color", UITheme.TextPrimary);
        ob.AddChild(_ordersTitle);
        _ordersStatus = new Label { ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
        _ordersStatus.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
        _ordersStatus.AddThemeColorOverride("font_color", UITheme.TextSecondary);
        ob.AddChild(_ordersStatus);
        _ordersBlocked = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(CommandWidth - 2 * CardPadX, 0),
            Visible = false,
        };
        _ordersBlocked.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
        _ordersBlocked.AddThemeColorOverride("font_color", UITheme.Warning);
        ob.AddChild(_ordersBlocked);
        _ordersBox = new VBoxContainer();
        _ordersBox.AddThemeConstantOverride("separation", 6);
        ob.AddChild(_ordersBox);
        _ordersNone = new Label { Text = "No orders to give here." };
        _ordersNone.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
        _ordersNone.AddThemeColorOverride("font_color", UITheme.TextDim);
        ob.AddChild(_ordersNone);
        MoveForceVerbsToOrders();

        // Destinations.
        var (dest, db) = MakeCard();
        _dispatchPanel = dest;
        _dispatchPanel.Visible = false;
        _commandColumn.AddChild(_dispatchPanel);

        var head = new HBoxContainer();
        db.AddChild(head);
        _dispatchTitle = MakeCardHeading("DESTINATIONS");
        head.AddChild(_dispatchTitle);
        _dispatchCollapseBtn = new Button { Text = "−", CustomMinimumSize = new Vector2(26, 22) };
        _dispatchCollapseBtn.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
        UITheme.ApplyButtonStyle(_dispatchCollapseBtn, isPrimary: false);
        _dispatchCollapseBtn.TooltipText = "Fold the list away. The rings stay on the map.";
        _dispatchCollapseBtn.Pressed += () =>
        {
            _dispatchCollapsed = !_dispatchCollapsed;
            ApplyDispatchCollapse();
        };
        head.AddChild(_dispatchCollapseBtn);

        _dispatchScroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        db.AddChild(_dispatchScroll);
        _dispatchRows = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _dispatchRows.AddThemeConstantOverride("separation", 4);
        _dispatchScroll.AddChild(_dispatchRows);
    }

    /// <summary>The selected force's verbs, in the order a player reaches for
    /// them: the launch first, then movement, then the rest. Moved, not
    /// rebuilt: each keeps its handler and RefreshPieceChrome still decides
    /// whether it shows.</summary>
    private void MoveForceVerbsToOrders()
    {
        var verbs = new Button[]
        {
            _sortieBtn, _partyDeployBtn, _interveneBtn, _marchModeBtn, _postBtn,
            _audienceBtn, _stopWorkBtn, _collectBtn, _recallBtn, _bankFurnaceBtn,
        };
        foreach (var b in verbs)
        {
            if (b == null || !IsInstanceValid(b))
            {
                continue;
            }
            b.GetParent()?.RemoveChild(b);
            b.CustomMinimumSize = new Vector2(0, 36);
            b.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            b.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
            _ordersBox.AddChild(b);
        }
    }

    private void ApplyDispatchCollapse()
    {
        if (_dispatchCollapseBtn != null)
        {
            _dispatchCollapseBtn.Text = _dispatchCollapsed ? "+" : "−";
        }
        if (_dispatchScroll != null)
        {
            _dispatchScroll.Visible = !_dispatchCollapsed;
        }
    }

    /// <summary>The selected force's destinations, or empty.</summary>
    private List<DispatchTarget> CurrentDispatchTargets(CycleState cycle)
    {
        if (cycle == null)
        {
            return new List<DispatchTarget>();
        }
        if (CastleSelected)
        {
            return DispatchTargets.ForCastle(cycle);
        }
        var party = SelectedParty();
        if (party == null || FieldPostings.IsDetachment(party))
        {
            return new List<DispatchTarget>();
        }
        return DispatchTargets.ForParty(cycle, party);
    }

    /// <summary>Refresh the Orders and Destinations cards and hand the atlas
    /// its rings. Called from RefreshPieceChrome after the verbs' visibility is
    /// decided and BEFORE SetPieces, so the one marker rebuild draws the rings.</summary>
    private void RefreshDispatch()
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        bool cityNow = _atlas3D?.CityMode ?? false;

        var targets = cityNow || NothingSelected ? new List<DispatchTarget>() : CurrentDispatchTargets(cycle);
        ApplyRangeFocus(cycle, cityNow);
        RefreshTimeBar(cycle);

        if (_atlas3D != null)
        {
            var marks = new List<WorldAtlas3D.DispatchMark>();
            foreach (var t in targets)
            {
                if (!t.CanGo)
                {
                    continue;
                }
                marks.Add(new WorldAtlas3D.DispatchMark
                {
                    Tile = new Vector2I(t.X, t.Y),
                    Label = t.ShortCost,
                    StepThrough = t.Method == DispatchMethod.StepThrough,
                });
            }
            _atlas3D.DispatchMarks = marks;
        }

        // The campus is not the place for field orders: in the city the column
        // keeps only the lens and the way back out.
        if (_forcesRows != null)
        {
            _forcesRows.Visible = !cityNow;
        }
        bool show = !cityNow && cycle != null && !NothingSelected;
        if (_ordersCard != null)
        {
            _ordersCard.Visible = show;
        }
        if (_dispatchPanel == null)
        {
            return;
        }
        if (!show)
        {
            _dispatchPanel.Visible = false;
            return;
        }

        RefreshOrdersCard(cycle);

        foreach (var child in _dispatchRows.GetChildren())
        {
            _dispatchRows.RemoveChild(child);
            child.QueueFree();
        }

        int reachable = 0, unreachable = 0;
        foreach (var t in targets)
        {
            if (!t.CanGo)
            {
                unreachable++;
                continue;
            }
            AddDispatchRow(t);
            reachable++;
        }
        if (unreachable > 0 && reachable > 0)
        {
            var more = new Label
            {
                Text = $"{unreachable} more out of reach",
                MouseFilter = Control.MouseFilterEnum.Stop,
            };
            more.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
            more.AddThemeColorOverride("font_color", UITheme.TextDim);
            var first = targets.Find(t => !t.CanGo);
            more.TooltipText = first != null ? $"{first.Name}: {first.Why}" : "";
            _dispatchRows.AddChild(more);
        }

        // A card with nothing in it is not shown at all: no "nowhere to go"
        // placeholder holding a slab of screen.
        _dispatchPanel.Visible = reachable > 0;
        _dispatchTitle.Text = $"DESTINATIONS  ·  {reachable}";
        int rowsHeight = reachable * (DestinationRowHeight + 4) + (unreachable > 0 ? 22 : 0);
        _dispatchScroll.CustomMinimumSize = new Vector2(0, Mathf.Min(rowsHeight, DestinationsMaxHeight));
        ApplyDispatchCollapse();
    }

    /// <summary>Name, one-line state, the reason it cannot act if there is one,
    /// and "no orders" when every verb is hidden.</summary>
    private void RefreshOrdersCard(CycleState cycle)
    {
        var party = SelectedParty();
        if (CastleSelected)
        {
            _ordersTitle.Text = "■  The castle";
            _ordersTitle.AddThemeColorOverride("font_color", UITheme.ArcaneBlue);
            _ordersStatus.Text = CastleStatusLine(cycle);
        }
        else if (party != null)
        {
            _ordersTitle.Text = (FieldPostings.IsDetachment(party) ? "▸  " : "▲  ") + party.Name;
            _ordersTitle.AddThemeColorOverride("font_color", UITheme.Gold);
            _ordersStatus.Text = PartyStatusLine(cycle, party);
        }
        _ordersStatus.TooltipText = _ordersStatus.Text;

        string blocked = null;
        if (CastleSelected)
        {
            if (!CastleMarch.CanMarchAtAll(cycle, out string castleWhy) && !CanSortie(cycle, out _))
            {
                blocked = castleWhy;
            }
        }
        else if (party != null && !FieldPostings.IsDetachment(party)
                 && !FieldMarch.CanOrderAtAll(cycle, party, out string partyWhy))
        {
            blocked = partyWhy;
            if (CanSigilRecallHome(cycle, party))
            {
                blocked += " The Teleport Sigil can still call them home: press Move party.";
            }
        }
        _ordersBlocked.Text = blocked ?? "";
        _ordersBlocked.Visible = !string.IsNullOrEmpty(blocked);

        bool anyVerb = false;
        foreach (var child in _ordersBox.GetChildren())
        {
            if (child is Control c && c.Visible)
            {
                anyVerb = true;
                break;
            }
        }
        _ordersBox.Visible = anyVerb;
        _ordersNone.Visible = !anyVerb && string.IsNullOrEmpty(blocked);
    }

    private void AddDispatchRow(DispatchTarget t)
    {
        var btn = new Button
        {
            Text = $"{t.Name}\n{t.CostLine}",
            Alignment = HorizontalAlignment.Left,
            CustomMinimumSize = new Vector2(0, DestinationRowHeight),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            ClipText = true,
            TooltipText = $"({t.X},{t.Y})",
        };
        btn.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
        UITheme.ApplyButtonStyle(btn, isPrimary: false);
        if (t.Method == DispatchMethod.StepThrough)
        {
            btn.AddThemeColorOverride("font_color", UITheme.ArcaneBlue);
        }
        var tile = new Vector2I(t.X, t.Y);
        btn.MouseEntered += () => _atlas3D?.HighlightDispatch(tile);
        btn.MouseExited += () => _atlas3D?.HighlightDispatch(null);
        btn.Pressed += () =>
        {
            _atlas3D?.HighlightDispatch(tile);
            if (_atlas3D != null)
            {
                _atlas3D.FlyToTile(t.X, t.Y, _atlas3D.CurrentCamDist);
            }
            DispatchTo(t, null);
        };
        _dispatchRows.AddChild(btn);
    }

    /// <summary>Give the order for one target. A step-through gets its own
    /// confirm (with "and take the field"); a walk and a march go through the
    /// confirms that already exist for them, so their wording and their
    /// refusals stay in one place.</summary>
    private void DispatchTo(DispatchTarget t, Vector2I? clicked)
    {
        if (t == null)
        {
            return;
        }
        switch (t.Method)
        {
            case DispatchMethod.StepThrough:
                OpenStepThroughConfirm(t, clicked);
                break;
            case DispatchMethod.OnFoot:
                TryMovePartyTo(t.X, t.Y);
                break;
            default:
                TryMarchTo(t.X, t.Y);
                break;
        }
    }

    /// <summary>Place card and map snap: if the selected party can step
    /// through to (x,y), open that order and say so.</summary>
    private bool TryDispatchTo(int x, int y)
    {
        if (CastleSelected)
        {
            return false;
        }
        var cycle = SaveManager.ActiveSave?.Cycle;
        var t = DispatchTargets.At(CurrentDispatchTargets(cycle), x, y);
        if (t == null || !t.CanGo || t.Method != DispatchMethod.StepThrough)
        {
            return false;
        }
        OpenStepThroughConfirm(t, null);
        return true;
    }

    /// <summary>Move armed, party selected: a click near a waystone means the
    /// waystone. False when nothing is near, and the click is an ordinary
    /// tile.</summary>
    private bool TryDispatchSnap(int col, int row)
    {
        if (CastleSelected)
        {
            return false;
        }
        var cycle = SaveManager.ActiveSave?.Cycle;
        var t = DispatchTargets.SnapAt(_world, CurrentDispatchTargets(cycle), col, row);
        if (t == null)
        {
            return false;
        }
        OpenStepThroughConfirm(t, new Vector2I(col, row));
        return true;
    }

    private void OpenStepThroughConfirm(DispatchTarget t, Vector2I? clicked)
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        var party = SelectedParty();
        if (cycle == null || party == null)
        {
            return;
        }

        var dlg = new ConfirmationDialog
        {
            Title = $"Step through to {t.Name}",
            OkButtonText = "Step through",
            DialogText = $"{party.Name} steps through to {t.Name} at ({t.X},{t.Y}). Instant, and free: "
                       + "the stone was paid for when it was raised.",
        };
        var field = dlg.AddButton("Step through and take the field", true, "field");
        field.Disabled = !t.CanTakeFieldOnArrival;
        field.TooltipText = t.CanTakeFieldOnArrival ? "" : "They are still busy, so they can arrive but not set out yet.";

        bool offerWalk = clicked.HasValue
                         && (clicked.Value.X != t.X || clicked.Value.Y != t.Y)
                         && FieldMarch.CanMarchTo(cycle, party, clicked.Value.X, clicked.Value.Y, out _);
        if (offerWalk)
        {
            dlg.AddButton($"Walk to ({clicked.Value.X},{clicked.Value.Y}) instead", false, "walk");
        }

        dlg.Confirmed += () =>
        {
            dlg.QueueFree();
            StepThrough(t, takeField: false);
        };
        dlg.CustomAction += action =>
        {
            dlg.QueueFree();
            string a = action.ToString();
            if (a == "field")
            {
                StepThrough(t, takeField: true);
            }
            else if (a == "walk" && clicked.HasValue)
            {
                TryMovePartyTo(clicked.Value.X, clicked.Value.Y);
            }
        };
        dlg.Canceled += () => dlg.QueueFree();
        AddChild(dlg);
        dlg.PopupCentered();
    }

    private void StepThrough(DispatchTarget t, bool takeField)
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        var party = SelectedParty();
        if (cycle == null || party == null)
        {
            return;
        }
        if (!FieldMarch.CanTeleportTo(cycle, party, t.X, t.Y, out string why))
        {
            ShowStrategicNotice("They cannot step through", why);
            return;
        }
        string jump = FieldMarch.Teleport(cycle, party, t.X, t.Y);
        if (string.IsNullOrEmpty(jump))
        {
            return;
        }
        cycle.PendingSiegeReports ??= new List<string>();
        cycle.PendingSiegeReports.Add(jump);
        SaveManager.MarkDirty();
        SaveManager.SaveIfDirty();
        if (_marchModeBtn != null)
        {
            _marchModeBtn.ButtonPressed = false;
        }
        _marchMode = false;
        _atlas3D?.HighlightDispatch(null);
        RefreshPieceChrome();
        if (takeField)
        {
            OnPartyTakeTheField();
        }
    }

    // ── Focus: fly onto a force and show its reach (2026-09-29) ───────────

    private bool _rangeFocus;
    private PanelContainer _rangeReadout;
    private Label _rangeReadoutText;

    /// <summary>Select a force, swing the camera onto it framed to its reach,
    /// and wash the tiles it can get to. Map piece clicks and roster rows both
    /// come here.</summary>
    private void FocusForce(string pieceId)
    {
        _rangeFocus = true;
        SelectPiece(pieceId);   // RefreshPieceChrome -> RefreshDispatch -> ApplyRangeFocus
        var cycle = SaveManager.ActiveSave?.Cycle;
        if (_atlas3D != null && cycle != null && !_atlas3D.CityMode)
        {
            var (center, radius, _, _) = RangeFor(cycle);
            if (center.HasValue)
            {
                // Negative: the tile lands right of centre, clear of the left panels.
                _atlas3D.FrameRange(center.Value, radius, -150f);
            }
        }
    }

    private void ClearRangeFocus()
    {
        if (!_rangeFocus)
        {
            return;
        }
        _rangeFocus = false;
        _atlas3D?.ClearRangeOverlay();
        if (_rangeReadout != null)
        {
            _rangeReadout.Visible = false;
        }
    }

    /// <summary>The selected force's reach: where it stands, how far it can go
    /// by its own means now, the tint, and one line saying so. The castle's
    /// reach is its fuel; a party's is the walk it can finish before the new
    /// moon (step-throughs go anywhere, and the rings already show those).</summary>
    private (Vector2I? center, int radius, Color tint, string line) RangeFor(CycleState cycle)
    {
        if (cycle?.World == null)
        {
            return (null, 0, UITheme.Gold, "");
        }
        if (CastleSelected)
        {
            if (!cycle.World.InBounds(cycle.CastleX, cycle.CastleY))
            {
                return (null, 0, UITheme.ArcaneBlue, "");
            }
            var at = new Vector2I(cycle.CastleX, cycle.CastleY);
            if (!CastleMarch.CanMarchAtAll(cycle, out string why))
            {
                return (at, 0, UITheme.ArcaneBlue, $"THE CASTLE  ·  cannot march: {why}");
            }
            int tiles = CastleMarch.RangeInTiles(cycle);
            return (at, tiles, UITheme.ArcaneBlue,
                $"THE CASTLE  ·  marches up to {tiles} tile(s) on {cycle.CastleFuel} fuel");
        }

        var party = SelectedParty();
        if (party == null || party.X < 0 || party.Y < 0)
        {
            return (null, 0, UITheme.Gold, "");
        }
        var here = new Vector2I(party.X, party.Y);
        string name = party.Name.ToUpperInvariant();
        if (FieldPostings.IsDetachment(party))
        {
            return (here, 0, UITheme.Gold, $"{name}  ·  holds its post");
        }
        if (!FieldMarch.CanOrderAtAll(cycle, party, out string partyWhy))
        {
            return (here, 0, UITheme.Gold, $"{name}  ·  {partyWhy}");
        }
        int days = cycle.Calendar?.DaysToNewMoon ?? CalendarState.DaysPerLunation;
        int walk = Mathf.Max(1, days / Mathf.Max(1, FieldMarch.DaysPerTile));
        int steps = 0;
        foreach (var t in DispatchTargets.ForParty(cycle, party))
        {
            if (t.CanGo && t.Method == DispatchMethod.StepThrough)
            {
                steps++;
            }
        }
        return (here, walk, UITheme.Gold,
            $"{name}  ·  walks up to {walk} tile(s) before the new moon ({days} day(s))"
            + (steps > 0 ? $"  ·  {steps} waystone(s) to step through" : ""));
    }

    /// <summary>Re-apply the wash and the line for whatever is selected now.
    /// Called from RefreshDispatch, so it follows every change of selection
    /// and every order.</summary>
    private void ApplyRangeFocus(CycleState cycle, bool cityNow)
    {
        if (!_rangeFocus || cityNow || cycle == null)
        {
            _atlas3D?.ClearRangeOverlay();
            if (_rangeReadout != null)
            {
                _rangeReadout.Visible = false;
            }
            return;
        }
        var (center, radius, tint, line) = RangeFor(cycle);
        if (!center.HasValue)
        {
            _atlas3D?.ClearRangeOverlay();
            if (_rangeReadout != null)
            {
                _rangeReadout.Visible = false;
            }
            return;
        }
        _atlas3D?.SetRangeOverlay(center.Value, radius, tint);
        EnsureRangeReadout();
        _rangeReadoutText.Text = line;
        _rangeReadoutText.AddThemeColorOverride("font_color", tint);
        _rangeReadout.Visible = true;
    }

    /// <summary>The line across the bottom of the map, above the hint: whose
    /// reach is lit and what it is. The close button drops the wash.</summary>
    private void EnsureRangeReadout()
    {
        if (_rangeReadout != null || _hud == null)
        {
            return;
        }
        _rangeReadout = new PanelContainer
        {
            AnchorLeft = 0.5f,
            AnchorRight = 0.5f,
            AnchorTop = 1f,
            AnchorBottom = 1f,
            GrowHorizontal = Control.GrowDirection.Both,
            GrowVertical = Control.GrowDirection.Begin,
            OffsetTop = -92,
            OffsetBottom = -44,
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        _rangeReadout.AddThemeStyleboxOverride("panel",
            UITheme.MakePanelStyle(UITheme.BgRaised, UITheme.CampusTitleBarBorder));
        _hud.AddChild(_rangeReadout);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 16);
        margin.AddThemeConstantOverride("margin_right", 8);
        margin.AddThemeConstantOverride("margin_top", 6);
        margin.AddThemeConstantOverride("margin_bottom", 6);
        _rangeReadout.AddChild(margin);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);
        margin.AddChild(row);

        _rangeReadoutText = new Label { VerticalAlignment = VerticalAlignment.Center };
        _rangeReadoutText.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize - 2);
        row.AddChild(_rangeReadoutText);

        var close = new Button { Text = "×", CustomMinimumSize = new Vector2(30, 28) };
        UITheme.ApplyButtonStyle(close, isPrimary: false);
        close.TooltipText = "Stop showing this force's reach.";
        close.Pressed += ClearRangeFocus;
        row.AddChild(close);
    }

    // ── The clock (2026-09-29) ────────────────────────────────────────────
    //    Top centre of the map: the date and the year on the left, the big
    //    button that passes until the next thing (and says what it is), and a
    //    one-day step. Carries everything the gold calendar card used to, so
    //    that card is gone and the date is said once on this screen.

    private PanelContainer _timeBar;
    private Label _timeDateLabel;
    private Label _timeYearLabel;
    private Button _passOneDayBtn;

    /// <summary>Called from BuildForcesPanel in place of docking the Pass time
    /// button into the roster. Takes over <c>_turnMoonBtn</c>.</summary>
    private void BuildTimeBar()
    {
        if (_hud == null || _turnMoonBtn == null || _timeBar != null)
        {
            return;
        }
        _timeBar = new PanelContainer
        {
            AnchorLeft = 0.5f,
            AnchorRight = 0.5f,
            AnchorTop = 0f,
            AnchorBottom = 0f,
            GrowHorizontal = Control.GrowDirection.Both,
            OffsetTop = HudManager.BarHeight + 8,
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        _timeBar.AddThemeStyleboxOverride("panel", CardStyle());
        _hud.AddChild(_timeBar);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", CardPadX + 2);
        margin.AddThemeConstantOverride("margin_right", CardPadX - 4);
        margin.AddThemeConstantOverride("margin_top", 8);
        margin.AddThemeConstantOverride("margin_bottom", 8);
        _timeBar.AddChild(margin);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);
        margin.AddChild(row);

        var dates = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        dates.AddThemeConstantOverride("separation", 2);
        row.AddChild(dates);
        _timeDateLabel = new Label();
        _timeDateLabel.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize);
        _timeDateLabel.AddThemeColorOverride("font_color", UITheme.Gold);
        dates.AddChild(_timeDateLabel);
        _timeYearLabel = new Label();
        _timeYearLabel.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
        _timeYearLabel.AddThemeColorOverride("font_color", UITheme.TextSecondary);
        dates.AddChild(_timeYearLabel);

        _turnMoonBtn.GetParent()?.RemoveChild(_turnMoonBtn);
        _turnMoonBtn.CustomMinimumSize = new Vector2(230, 52);
        _turnMoonBtn.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
        UITheme.ApplyButtonStyle(_turnMoonBtn, isPrimary: true);
        row.AddChild(_turnMoonBtn);

        _passOneDayBtn = new Button { Text = "+1 day", CustomMinimumSize = new Vector2(72, 52) };
        _passOneDayBtn.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
        UITheme.ApplyButtonStyle(_passOneDayBtn, isPrimary: false);
        _passOneDayBtn.TooltipText = "Let a single day pass. Still stops for anything that happens on it.";
        _passOneDayBtn.Pressed += () =>
        {
            var cycle = SaveManager.ActiveSave?.Cycle;
            if (cycle?.Calendar != null)
            {
                PassTime(cycle, 1);
            }
        };
        row.AddChild(_passOneDayBtn);

        // Filled now: the HUD is built after the last RefreshPieceChrome of the
        // load, and the city view (where the map opens) would otherwise show a
        // blank clock and an unfilled column until something else refreshed
        // them. BuildForcesPanel calls this after the roster exists.
        RefreshDispatch();
    }

    /// <summary>The date, the year, the next stop and the button's count.
    /// Called from RefreshDispatch, after RefreshPieceChrome has written its
    /// own button text, so this wording wins.</summary>
    private void RefreshTimeBar(CycleState cycle)
    {
        if (_timeBar == null || cycle?.Calendar == null)
        {
            return;
        }
        var cal = cycle.Calendar;
        _timeBar.Visible = true;   // the clock runs in the city too
        _timeDateLabel.Text = $"Lunation {cal.CurrentLunation} of {cal.LunationsPerCycle}  ·  "
                            + $"day {cal.DayOfLunation + 1}  ·  {cal.CurrentMoonName}";

        // The year line: what the gold card said, in one line.
        int left = cal.LunationsRemaining;
        string conj = left <= 2 ? $"{left} lunation(s) to the Conjunction" : $"{left} lunations to the Conjunction";
        int year = cycle.CampaignYear;
        if (year > 1)
        {
            int foePct = Mathf.RoundToInt(cycle.SeasonalThreatLevel * CampaignEscalation.ThreatDifficultyStep * 100f);
            _timeYearLabel.Text = $"Year {year}  ·  foes +{foePct}%  ·  {conj}";
        }
        else
        {
            _timeYearLabel.Text = $"Year 1  ·  a pristine timeline  ·  {conj}";
        }
        _timeYearLabel.AddThemeColorOverride("font_color",
            left <= 2 || year > 1 ? UITheme.Danger : UITheme.TextSecondary);

        int now = cal.AbsoluteDay;
        int nextDay = WorldClock.NextEventDay(cycle, out string what);
        int moonIn = cal.DaysToNewMoon;
        bool stopsAtEvent = nextDay > 0 && nextDay - now < moonIn;
        int days = stopsAtEvent ? nextDay - now : moonIn;
        string until = stopsAtEvent ? what : "the new moon";
        if (_turnMoonBtn != null)
        {
            _turnMoonBtn.Text = $"☽  Pass {days} day(s)\nuntil {until}";
            _turnMoonBtn.TooltipText = $"Let the days pass until {until}. Stops early for anything that "
                                     + "happens on the way, and always at the new moon.";
        }
    }

    /// <summary>Double click on the world map, anywhere (2026-09-29): nothing
    /// has the orders, the reach and the rings go, any armed Move is put away,
    /// and the camera pulls back to the whole world. The first click of the
    /// pair has already done its own thing (selected a piece, say); this
    /// undoes the selection and flies out over it.</summary>
    private void OnMapDoubleClicked()
    {
        if (_atlas3D == null || _atlas3D.CityMode || _deployUi != null)
        {
            return;
        }
        ClearRangeFocus();
        if (_marchModeBtn != null)
        {
            _marchModeBtn.ButtonPressed = false;
        }
        _marchMode = false;
        _atlas3D.HighlightDispatch(null);
        SelectPiece(NoPieceId);
        _atlas3D.FlyToOverview();
    }

    /// <summary>A piece was clicked on the map. Unarmed: select it. Armed:
    /// its tile is the destination, except the piece taking the order, where
    /// the click means "never mind".</summary>
    private void OnAtlasPiecePicked(string pieceId, int col, int row)
    {
        if (_atlas3D != null && _atlas3D.CityMode)
        {
            return;
        }
        if (_deployUi != null)
        {
            return;
        }
        string id = pieceId == WorldAtlas3D.CastlePieceId ? "" : pieceId;
        if (_marchMode)
        {
            if (id == _selectedPieceId)
            {
                if (_marchModeBtn != null)
                {
                    _marchModeBtn.ButtonPressed = false;
                }
                _marchMode = false;
                return;
            }
            OnAtlas3DTilePicked(col, row);
            return;
        }
        FocusForce(id);
    }
}
