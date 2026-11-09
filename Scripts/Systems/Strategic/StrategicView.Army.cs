using Godot;
using System.Collections.Generic;

// ============================================================
// StrategicView.Army.cs  (partial of StrategicView)
//
// Purpose:        The guild's army on the strategic map (docs/provinces_
//                 v1_12_troops.md, slice PV7, PR94): the province
//                 card's rows (what the province's ground raises, and a
//                 Raise button for each line it can raise now), and the
//                 Army button beside Maneuvers, whose panel lists every
//                 line with its strength, rank, numbers and upkeep, its
//                 doctrine to choose, and Disband.
// Layer:          UI (strategic view)
// Collaborators:  ArmyMuster.cs (raising, the lines, the words),
//                 TroopLines.cs (numbers and doctrines),
//                 StrategicView.ProvinceCard.cs (the card: CardLine,
//                 _provinceCardIndex, RefreshProvinceCard), StrategicView.cs
//                 (MakeCityChromeButton, ShowStrategicNotice,
//                 RefreshPieceChrome)
// See:            docs/provinces_v1_12_troops.md section 6
// ============================================================

public partial class StrategicView
{
    private const string ArmyGlyph = "\u2694";

    /// <summary>How many Raise buttons the card keeps: no province raises more than three lines
    /// (Lowland ground and a city: Pikes, Outriders and Sappers).</summary>
    private const int CardRaiseSlots = 3;

    private Button _armyBtn;   // provinces PV7: the army, beside the maneuvers
    private Label _cardArmyLine;
    private readonly List<Button> _cardRaiseButtons = new();
    private readonly List<string> _cardRaiseLineIds = new();

    // ══════════════════════════════════════════════════════════════════════
    // The province card's rows
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>Built once with the card, under what holding gives: a heading, the army line, and
    /// the Raise buttons, hidden until a province fills them.</summary>
    private void BuildArmyCardRows(VBoxContainer stack)
    {
        stack.AddChild(new HSeparator { MouseFilter = Control.MouseFilterEnum.Ignore });
        CardLine(stack, UITheme.TextPrimary, "The army");
        _cardArmyLine = CardLine(stack, UITheme.TextSecondary);
        _cardRaiseButtons.Clear();
        _cardRaiseLineIds.Clear();
        for (int i = 0; i < CardRaiseSlots; i++)
        {
            int slot = i;
            var button = new Button { FocusMode = Control.FocusModeEnum.None, Visible = false };
            button.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
            UITheme.ApplyButtonStyle(button, isPrimary: false);
            button.Pressed += () => OnRaiseLinePressed(slot);
            stack.AddChild(button);
            _cardRaiseButtons.Add(button);
            _cardRaiseLineIds.Add("");
        }
    }

    /// <summary>Fill the rows from the card's province: what its ground raises, and a button for
    /// each such line while the province is held, lit when it can be raised now.</summary>
    private void RefreshArmyCardRows(CycleState cycle, Province province)
    {
        if (_cardArmyLine == null)
            return;
        bool musters = ArmyMuster.Musters(cycle) && province != null;
        var defs = musters ? ArmyMuster.RaisedFrom(cycle.World, province) : new List<TroopLineDef>();
        SetCardLine(_cardArmyLine, musters ? ArmyMuster.CardLine(cycle, province, defs) : "");
        bool held = musters && ProvinceBenefits.HeldForBenefits(cycle, province);
        for (int i = 0; i < _cardRaiseButtons.Count; i++)
        {
            var button = _cardRaiseButtons[i];
            if (!held || i >= defs.Count)
            {
                button.Visible = false;
                _cardRaiseLineIds[i] = "";
                continue;
            }
            var def = defs[i];
            _cardRaiseLineIds[i] = def.Id;
            bool can = ArmyMuster.CanRaise(cycle, province, def, out string why);
            button.Text = $"Raise {def.Name}  ({def.RaiseCost} supplies, upkeep {def.Upkeep})";
            button.Disabled = !can;
            button.TooltipText = can
                ? $"{def.Role} HP {def.Hp}, armour {def.Armor}, damage {def.Damage}, reach {def.Range}, move {def.Move}."
                : why;
            button.Visible = true;
        }
    }

    private void OnRaiseLinePressed(int slot)
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        var world = cycle?.World;
        int index = _provinceCardIndex;
        if (world == null || index < 0 || index >= world.Provinces.Count || slot < 0 || slot >= _cardRaiseLineIds.Count)
            return;
        string lineId = _cardRaiseLineIds[slot];
        if (string.IsNullOrEmpty(lineId))
            return;
        var raised = ArmyMuster.Raise(cycle, world.Provinces[index], lineId, out string line);
        if (raised == null)
        {
            ShowStrategicNotice("No line raised", line);
            return;
        }
        SaveManager.MarkDirty();
        SaveManager.SaveIfDirty();
        RefreshProvinceCard();
        RefreshPieceChrome();
        ShowStrategicNotice("A line is raised", line);
    }

    // ══════════════════════════════════════════════════════════════════════
    // The Army button and panel
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>The Army button, beside Maneuvers. RefreshArmyButton owns its visibility.</summary>
    private void BuildArmyButton()
    {
        _armyBtn = MakeCityChromeButton($"{ArmyGlyph}  Army", primary: false);
        _armyBtn.Visible = false;
        _armyBtn.Pressed += ShowArmy;
    }

    /// <summary>Shown for the castle on the world map where the world has provinces: the army
    /// marches with the castle.</summary>
    private void RefreshArmyButton(CycleState cycle, bool cityNow)
    {
        if (_armyBtn == null)
            return;
        bool show = CastleSelected && !cityNow && ArmyMuster.Musters(cycle);
        _armyBtn.Visible = show;
        if (!show)
            return;
        int size = ArmyMuster.Size(cycle), capacity = ArmyMuster.Capacity(cycle);
        _armyBtn.Text = $"{ArmyGlyph}  Army  ({size} of {capacity})";
        _armyBtn.TooltipText = size == 0
            ? "No line is raised yet. A held province's card raises the lines its ground breeds."
            : $"{size} line(s), upkeep {ArmyMuster.UpkeepTotal(cycle)} supplies at each new moon. "
              + "They stand garrison when the castle is assaulted.";
    }

    /// <summary>The army panel: every line, its numbers, its doctrine to choose, and Disband.</summary>
    private void ShowArmy()
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        if (!ArmyMuster.Musters(cycle))
        {
            ShowStrategicNotice("The army", "The army musters only where the world has provinces.");
            return;
        }

        var dlg = new AcceptDialog
        {
            Title = "The army",
            OkButtonText = "Close",
            MinSize = new Vector2I(620, 460),
        };
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);
        dlg.AddChild(box);

        var head = DialogFit.Wrap(new Label());
        head.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
        head.AddThemeColorOverride("font_color", UITheme.TextSecondary);
        box.AddChild(head);

        var scroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(580, 320),
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        box.AddChild(scroll);
        var rows = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        rows.AddThemeConstantOverride("separation", 12);
        scroll.AddChild(rows);

        void Fill()
        {
            foreach (Node child in rows.GetChildren())
                child.QueueFree();
            head.Text = ArmyMuster.Summary(cycle);
            if (cycle.Army.Lines.Count == 0)
            {
                var none = DialogFit.Wrap(new Label { Text = "No line is raised yet. Open a held province's card: its ground raises its lines." });
                none.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
                rows.AddChild(none);
                return;
            }
            foreach (var line in cycle.Army.Lines.ToArray())
            {
                var def = TroopLines.Get(line?.LineId);
                if (def == null)
                    continue;
                string lineStateId = line.Id;
                var stats = TroopLines.Stats(def, line);

                var row = new VBoxContainer();
                row.AddThemeConstantOverride("separation", 4);
                rows.AddChild(row);

                var title = DialogFit.Wrap(new Label
                {
                    Text = $"{ArmyMuster.LineName(cycle, line)}: strength {line.Strength}, "
                           + $"{TroopLines.RankName(line.Battles)} ({line.Battles} battle(s)), upkeep {def.Upkeep}",
                });
                title.AddThemeColorOverride("font_color", UITheme.Gold);
                row.AddChild(title);

                var numbers = DialogFit.Wrap(new Label
                {
                    Text = $"{def.Role} HP {stats.Hp} of {stats.MaxHp}, armour {stats.Armor}, damage {stats.Damage}, "
                           + $"reach {stats.Range}, move {stats.Move}. "
                           + $"{TroopLines.DoctrineName(def, line.Doctrine)}: {TroopLines.DoctrineText(def, line.Doctrine)}",
                });
                numbers.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
                numbers.AddThemeColorOverride("font_color", UITheme.TextSecondary);
                row.AddChild(numbers);

                var controls = new HBoxContainer();
                controls.AddThemeConstantOverride("separation", 8);
                row.AddChild(controls);

                var doctrineLabel = new Label { Text = "Doctrine:" };
                doctrineLabel.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
                controls.AddChild(doctrineLabel);
                var doctrine = new OptionButton();
                for (int i = 0; i < def.Doctrines.Length; i++)
                    doctrine.AddItem(def.Doctrines[i], i);
                doctrine.Selected = TroopLines.DoctrineIndex(def, line.Doctrine);
                doctrine.ItemSelected += index =>
                {
                    if (!ArmyMuster.SetDoctrine(cycle, lineStateId, (int)index))
                        return;
                    SaveManager.MarkDirty();
                    SaveManager.SaveIfDirty();
                    Fill();
                };
                controls.AddChild(doctrine);

                var disband = new Button { Text = "Disband", FocusMode = Control.FocusModeEnum.None };
                UITheme.ApplyButtonStyle(disband, isPrimary: false);
                disband.Pressed += () =>
                {
                    if (!ArmyMuster.Disband(cycle, lineStateId, out string said))
                        return;
                    SaveManager.MarkDirty();
                    SaveManager.SaveIfDirty();
                    GD.Print($"[Army] {said}");
                    RefreshPieceChrome();
                    Fill();
                };
                controls.AddChild(disband);
            }
        }

        Fill();
        dlg.Confirmed += () => dlg.QueueFree();
        dlg.Canceled += () => dlg.QueueFree();
        // Fixed size: the list scrolls inside it, and the Close button stays on the screen.
        DialogFit.PopupFixed(this, dlg, new Vector2I(640, 520));
    }
}
