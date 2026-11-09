using Godot;
using System.Collections.Generic;

// ============================================================
// StrategicView.Bastion.cs  (partial of StrategicView)
//
// Purpose:        The castle on the province graph, in the strategic
//                 view (docs/provinces_v1_9_bastion.md, slice PV4): the
//                 march's words (the notice when March is armed, the
//                 readout's line on each province, the confirm with its
//                 route hop by hop), the route the castle is seen to walk
//                 (seat to seat), and the Maneuvers button and dialog,
//                 where the font's spells are cast (PR73).
// Layer:          UI (strategic view)
// Collaborators:  CastleHops.cs (the plan), CastleManeuvers.cs (what can
//                 be cast, and casting it), CastleMarch.cs (the march
//                 itself), StrategicView.cs (the chrome, TryMarchTo,
//                 ExecuteCastleMarch, CorridorTiles, ShowStrategicNotice),
//                 StrategicView.ProvinceHover.cs (the readout)
// See:            docs/provinces_v1_9_bastion.md section 7
// ============================================================

public partial class StrategicView
{
    private const string Spark = "\u2726";

    private Button _maneuverBtn;   // provinces PV4: the font's maneuvers, beside the march

    // ══════════════════════════════════════════════════════════════════════
    // The march's words
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>The notice when March is armed for the castle.</summary>
    private string MarchModeNotice(CycleState cycle)
    {
        if (cycle == null || CastleHops.CastleProvince(cycle) < 0)
        {
            int range = cycle != null ? CastleMarch.RangeInTiles(cycle) : 0;
            return $"Click charted ground to send the fortress there. Range {range} tile(s) "
                   + $"on the fuel in the furnace, at {CastleMarch.FuelPerTile} per tile.";
        }
        string armed = CastleManeuvers.ArmedText(cycle);
        return "Click charted ground to send the fortress there. It marches province by province along "
               + $"the cheapest route: {CastleHops.HopBase} fuel a hop, more to enter rough ground, "
               + $"{CastleHops.FordCost} more where it must ford a river and {CastleHops.RidgeCost} more where it "
               + $"must climb a ridge, {CastleHops.RoadDiscount} less where a road crosses; "
               + $"{CastleHops.InProvinceCost} within its own province. The furnace holds {cycle.CastleFuel} fuel. "
               + "The readout shows each province's cost as you point at it."
               + (armed.Length > 0 ? $"\n\nArmed for this march: {armed}." : "");
    }

    /// <summary>The readout's line in march mode: what a march to this province costs.</summary>
    private string MarchHint(CycleState cycle, Province province)
    {
        if (!_marchMode || !CastleSelected || cycle == null || province == null)
            return "";
        var plan = CastleHops.Plan(cycle, province.SeatX, province.SeatY);
        if (plan == null)
            return "";
        if (!plan.Reachable)
            return " March: no land route reaches it.";
        if (plan.Hops.Count == 0)
            return $" March: within the castle's province, {plan.Fuel} fuel.";
        string tooFar = plan.Fuel > cycle.CastleFuel ? " Too far on this tank." : "";
        return $" March: {plan.Hops.Count} hop(s), {plan.Fuel} fuel of {cycle.CastleFuel}.{tooFar}";
    }

    /// <summary>The march confirm's words: the route hop by hop, the fuel, the camp.</summary>
    private string MarchConfirmText(CycleState cycle, int col, int row)
    {
        int cost = CastleMarch.CostTo(cycle, col, row);
        int days = CastleManeuvers.CampDaysPreview(cycle, col, row, CastleMarch.ArrivalCampDays);
        string camp = days > 0
            ? $"On arrival the crews make camp for {days} day(s), and the castle is exposed until they finish."
            : "The Wayfarer's Beacon brings it in among friends: no camp to make, and no refuel.";
        var plan = CastleHops.Plan(cycle, col, row);
        if (plan == null)
        {
            int tiles = cycle.World.HexDistance(cycle.CastleX, cycle.CastleY, col, row);
            return $"Send the castle {tiles} tile(s) to ({col},{row}).\n\nFuel: {cost} of {cycle.CastleFuel}.\n{camp}";
        }
        string dest = ProvinceControl.Name(cycle.World.Provinces[plan.ToProvince]);
        string armed = CastleManeuvers.ArmedText(cycle);
        string route = plan.Hops.Count == 0
            ? $"Within {dest}, to ({col},{row})."
            : $"To {dest}, ({col},{row}), in {plan.Hops.Count} hop(s):\n{CastleHops.Describe(cycle.World, plan)}";
        return $"{route}\n\nFuel: {cost} of {cycle.CastleFuel}."
               + (armed.Length > 0 ? $"\nArmed: {armed}." : "")
               + $"\n{camp}";
    }

    /// <summary>The tiles the castle is seen to walk: seat to seat along the route it was
    /// charged for, or the straight corridor where the old march applies.</summary>
    private List<Vector2I> MarchPath(int fromX, int fromY, int toX, int toY)
    {
        var plan = CastleHops.LastPlan;
        if (plan == null || plan.Waypoints.Count < 2
            || plan.Waypoints[0] != (fromX, fromY) || plan.Waypoints[plan.Waypoints.Count - 1] != (toX, toY))
        {
            return CorridorTiles(fromX, fromY, toX, toY);
        }
        var tiles = new List<Vector2I>();
        for (int i = 1; i < plan.Waypoints.Count; i++)
        {
            var (ax, ay) = plan.Waypoints[i - 1];
            var (bx, by) = plan.Waypoints[i];
            foreach (var tile in CorridorTiles(ax, ay, bx, by))
            {
                if (tiles.Count == 0 || tiles[tiles.Count - 1] != tile)
                    tiles.Add(tile);
            }
        }
        return tiles;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Maneuvers
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>The Maneuvers button, beside March and Sortie. RefreshManeuverButton owns its
    /// visibility.</summary>
    private void BuildManeuverButton()
    {
        _maneuverBtn = MakeCityChromeButton($"{Spark}  Maneuvers", primary: false);
        _maneuverBtn.Visible = false;
        _maneuverBtn.Pressed += ShowManeuvers;
    }

    /// <summary>Shown for the castle on the world map where the world has provinces; reads the
    /// font's Essence.</summary>
    private void RefreshManeuverButton(CycleState cycle, bool cityNow)
    {
        if (_maneuverBtn == null)
            return;
        bool show = CastleSelected && !cityNow && CastleHops.CastleProvince(cycle) >= 0;
        _maneuverBtn.Visible = show;
        if (!show)
            return;
        int known = CastleManeuvers.Known(cycle).Count;
        // Provinces 7j: live with nothing learned, and saying so; its notice tells where the
        // spells come from. Greyed out, it read as a control that did nothing.
        _maneuverBtn.Text = known == 0
            ? $"{Spark} Maneuvers (none known)"
            : $"{Spark} Maneuvers ({CastleManeuvers.Font(cycle)} Essence)";
        _maneuverBtn.Disabled = false;
        _maneuverBtn.TooltipText = known == 0
            ? "The font casts the overworld spells the guild knows, and it knows none yet."
            : $"Cast one of {known} maneuver(s) from the castle. The font fills again at the new moon.";
    }

    /// <summary>The Maneuvers dialog: pick a maneuver, read what it does and what it costs, aim
    /// it where it needs aiming, cast.</summary>
    private void ShowManeuvers()
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        var known = CastleManeuvers.Known(cycle);
        if (cycle == null || known.Count == 0)
        {
            // Provinces 7j: where the spells come from.
            ShowStrategicNotice("Maneuvers",
                "The font casts the castle's maneuvers from the overworld spells the guild knows: Force "
                + "Path, Veil, Retrace, Ley Leap and the rest. It knows none of them yet. Spells are "
                + "learned on expeditions, at lore places, in deals closed on cordial terms and from the "
                + "fallen, or from scrolls."
                + (OS.IsDebugBuild() ? "\n\nTo test: Settings, Developer tools, Learn Maneuvers." : ""));
            return;
        }

        var dlg = new ConfirmationDialog
        {
            Title = $"Maneuvers  (the font holds {CastleManeuvers.Font(cycle)} Essence)",
            OkButtonText = "Cast",
            CancelButtonText = "Close",
            MinSize = new Vector2I(560, 400),
        };
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);
        dlg.AddChild(box);

        var picker = new OptionButton();
        foreach (var def in known)
            picker.AddItem($"{def.Name}  ({CastleManeuvers.CostFor(cycle, def)} Essence)");
        picker.Selected = 0;
        box.AddChild(picker);

        var detail = DialogFit.Wrap(new Label());
        detail.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
        detail.AddThemeColorOverride("font_color", UITheme.TextSecondary);
        box.AddChild(detail);

        var targetRow = new HBoxContainer();
        targetRow.AddThemeConstantOverride("separation", 8);
        var targetLbl = new Label { Text = "Aim:" };
        targetLbl.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
        targetRow.AddChild(targetLbl);
        var targetPick = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        targetRow.AddChild(targetPick);
        box.AddChild(targetRow);

        var targets = new List<(string Key, string Label)>();
        void ShowChoice(int i)
        {
            var def = known[Mathf.Clamp(i, 0, known.Count - 1)];
            bool can = CastleManeuvers.CanCast(cycle, def, out string why);
            detail.Text = def.Text + (can ? "" : $"\n\nNot now: {why}");
            targets.Clear();
            targets.AddRange(CastleManeuvers.Targets(cycle, def));
            targetPick.Clear();
            foreach (var (_, label) in targets)
                targetPick.AddItem(label);
            targetRow.Visible = def.Target != ManeuverTarget.None && targets.Count > 0;
            if (targets.Count > 0)
                targetPick.Selected = 0;
        }
        ShowChoice(0);
        picker.ItemSelected += idx => ShowChoice((int)idx);

        dlg.Confirmed += () =>
        {
            var def = known[Mathf.Clamp(picker.Selected, 0, known.Count - 1)];
            string aim = def.Target != ManeuverTarget.None && targets.Count > 0
                ? targets[Mathf.Clamp(targetPick.Selected, 0, targets.Count - 1)].Key
                : "";
            dlg.QueueFree();
            if (!CastleManeuvers.Cast(cycle, def.SpellId, aim, out string line, out bool moved))
            {
                ShowStrategicNotice($"The font cannot cast {def.Name}", line);
                return;
            }
            SaveManager.MarkDirty();
            SaveManager.SaveIfDirty();
            if (moved)
            {
                // The castle or the map changed: the news carries the line, and the map opens afresh.
                cycle.PendingSiegeReports ??= new List<string>();
                cycle.PendingSiegeReports.Add(line);
                GetTree().ReloadCurrentScene();
                return;
            }
            RefreshPieceChrome();
            ShowStrategicNotice(def.Name, line);
        };
        dlg.Canceled += () => dlg.QueueFree();
        AddChild(dlg);
        dlg.PopupCentered();
    }
}
