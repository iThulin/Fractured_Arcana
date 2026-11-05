using Godot;
// ============================================================
// StrategicView.Places.cs: where a force stands, and the folded list (2026-11-05)
//
// Purpose:        Section 7h of the provinces spec
//                 (docs/provinces_v1_7h_map_fixes.md), the map fixes
//                 shelved at the end of 7f. Fix 7: the selection panel
//                 named a force's place by grid coordinates. It now names
//                 the province and the realm on a line of its own, and the
//                 tile's coordinates are that line's tooltip. The roster's
//                 chips say the place in their tooltips too. Fix 8: folded,
//                 Destinations was a full-width card holding one line. It
//                 now folds into a chip in the roster's row, beside Muster,
//                 and the chip opens it again.
// Layer:          UI (StrategicView partial)
// Collaborators:  StrategicView.cs (CastleStatusLine and PartyStatusLine say
//                 what a force is doing, no longer where; the roster's row
//                 holds the chip), StrategicView.Dispatch.cs (the orders card
//                 holds the place line; ApplyDispatchCollapse decides between
//                 the card and the chip)
// See:            docs/provinces_v1_7h_map_fixes.md
// ============================================================
public partial class StrategicView
{
    /// <summary>The separator the status lines use between their parts.</summary>
    private const string PartSep = "  \u00B7  ";

    /// <summary>The orders card's place line: province and realm, the coordinates in its tooltip.</summary>
    private Label _ordersPlace;
    /// <summary>The folded Destinations card, as a chip in the roster's row.</summary>
    private Button _destinationsChip;
    /// <summary>The selected force has somewhere it can go (RefreshDispatch).</summary>
    private bool _dispatchAvailable;
    /// <summary>How many places it can go.</summary>
    private int _dispatchReachable;

    // ── Fix 7: places by name ────────────────────────────────────────────

    /// <summary>A tile as a place: its province and the realm that holds it ("Crownland, The
    /// Boreal March"); "at sea"; the province alone in no realm; the coordinates in a world
    /// without provinces (an older save) or off the map.</summary>
    private string PlaceName(CycleState cycle, int x, int y, bool withRealm = true)
    {
        var world = cycle?.World;
        if (world == null || !world.InBounds(x, y))
            return $"({x},{y})";
        if (world.GetTile(x, y).IsWater)
            return "at sea";
        if (!world.HasProvinces())
            return $"({x},{y})";
        int index = world.ProvinceIndexAt(x, y);
        if (index < 0 || index >= world.Provinces.Count)
            return $"({x},{y})";
        var province = world.Provinces[index];
        string name = string.IsNullOrEmpty(province.Name) ? province.Id : province.Name;
        string realm = withRealm ? RealmName(province.KingdomId) : "";
        return realm.Length > 0 ? $"{name}, {realm}" : name;
    }

    /// <summary>A realm by the name its seat carries on the map, or "" for none.</summary>
    private string RealmName(string kingdomId)
    {
        if (string.IsNullOrEmpty(kingdomId))
            return "";
        if (_kingdoms != null && _kingdoms.TryGetValue(kingdomId, out var k) && k != null
            && !string.IsNullOrEmpty(k.DisplayName))
            return k.DisplayName;
        return kingdomId;
    }

    /// <summary>Where the castle stands, by name.</summary>
    private string CastlePlace(CycleState cycle)
        => cycle?.World != null && cycle.World.InBounds(cycle.CastleX, cycle.CastleY)
            ? PlaceName(cycle, cycle.CastleX, cycle.CastleY)
            : "nowhere yet";

    /// <summary>The tile a party is on: its dive's tile while it is in the field.</summary>
    private static (int x, int y) PartyTile(FieldParty p)
        => p.Sortie != null && p.Sortie.IsLive() ? (p.Sortie.X, p.Sortie.Y) : (p.X, p.Y);

    /// <summary>Where a party stands, by name, or "" when it is not on the map.</summary>
    private string PartyPlace(CycleState cycle, FieldParty p)
    {
        var (x, y) = PartyTile(p);
        return x < 0 || y < 0 ? "" : PlaceName(cycle, x, y);
    }

    /// <summary>A force's line for its chip's tooltip: where, then what.</summary>
    private static string ForceLine(string place, string status)
        => string.IsNullOrEmpty(place) ? status : place + PartSep + status;

    /// <summary>The orders card's place line for the selected force.</summary>
    private void RefreshOrdersPlace(CycleState cycle, FieldParty party)
    {
        if (_ordersPlace == null || !IsInstanceValid(_ordersPlace) || cycle == null)
            return;
        int x = -1, y = -1;
        string place = "";
        if (CastleSelected)
        {
            x = cycle.CastleX;
            y = cycle.CastleY;
            place = CastlePlace(cycle);
        }
        else if (party != null)
        {
            (x, y) = PartyTile(party);
            place = PartyPlace(cycle, party);
        }
        _ordersPlace.Text = place;
        _ordersPlace.Visible = place.Length > 0;
        _ordersPlace.TooltipText = x >= 0 && y >= 0 ? $"{place}\nTile ({x},{y})" : place;
    }

    // ── Fix 8: Destinations, folded, as a chip ───────────────────────────

    /// <summary>The chip that stands for the folded Destinations card, in the roster's row
    /// beside Muster. Hidden while the card is open or the force has nowhere to go.</summary>
    private Button BuildDestinationsChip()
    {
        _destinationsChip = new Button
        {
            Text = "Destinations",
            TooltipText = "Where the selected force can go, as a list. The rings on the map show the same places.",
            CustomMinimumSize = new Vector2(0, 34),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd,
            SizeFlagsVertical = Control.SizeFlags.ShrinkBegin,
            FocusMode = Control.FocusModeEnum.None,
            Visible = false,
        };
        _destinationsChip.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize - 3);
        UITheme.ApplyButtonStyle(_destinationsChip, isPrimary: false);
        _destinationsChip.Pressed += () =>
        {
            _dispatchCollapsed = false;
            ApplyDispatchCollapse();
        };
        return _destinationsChip;
    }

    /// <summary>Show the chip while the card is folded and the force can go somewhere.</summary>
    private void RefreshDestinationsChip()
    {
        if (_destinationsChip == null || !IsInstanceValid(_destinationsChip))
            return;
        _destinationsChip.Visible = _dispatchAvailable && _dispatchCollapsed;
        _destinationsChip.Text = $"Destinations{PartSep}{_dispatchReachable}";
    }
}
