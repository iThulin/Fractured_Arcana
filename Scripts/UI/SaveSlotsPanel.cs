using Godot;
using System;
using static CampusUi;
// ============================================================
// SaveSlotsPanel.cs: Load Game, on the title screen (2026-11-05)
//
// Purpose:        The save slots, moved from the Guild tab to the title
//                 screen. The Guild tab was reached at boot through a
//                 "Guild Hall" door that had no way back out, and its own
//                 comment already said slots are chrome, not a building:
//                 a building only exists once a save is loaded. Each slot
//                 loads (into the city, as Continue does), starts a new
//                 guild when empty (the New Game screen), or is deleted
//                 after a confirmation (the Guild tab deleted at once).
// Layer:          UI
// Collaborators:  TitleScreen.cs (opens it), SaveManager (slots, load,
//                 delete), PlayerSession (PendingNewGameSlot,
//                 StartInCityOnOpen), NewGameScreen (founds the guild)
// See:            docs/guild_screen_retirement_v1.md
// ============================================================
/// <summary>The save slots, as an overlay on the title screen.</summary>
public partial class SaveSlotsPanel : Control
{
    public const string StrategicScenePath = "res://Scenes/Overworld/StrategicScene.tscn";
    public const string NewGameScenePath = "res://Scenes/UI/NewGameScreen.tscn";

    /// <summary>A line above the slots saying why the list opened, or "".</summary>
    public string Note = "";

    private VBoxContainer _slots;
    private Label _noteLabel;
    /// <summary>A slot was deleted: the title's own buttons (Continue, Load Game) may be stale,
    /// so Back reloads the title rather than just closing.</summary>
    private bool _changed;

    /// <summary>The first slot with no guild in it, or -1 when every slot is taken.</summary>
    public static int FirstEmptySlot()
    {
        foreach (var slot in SaveManager.GetAllSlotInfo())
        {
            if (slot.IsEmpty)
                return slot.Slot;
        }
        return -1;
    }

    public override void _Ready()
    {
        // Anchors AND offsets, set outright. SetAnchorsPreset alone left this panel at its
        // minimum size in the top-left corner on the 4K Windows build (2026-11-13).
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;

        var bg = new ColorRect { Color = UITheme.WorldDeep, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(bg);
        bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        var center = new CenterContainer();
        AddChild(center);
        center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        var column = MakeVBox(14);
        column.CustomMinimumSize = new Vector2(820, 0);
        center.AddChild(column);

        var title = new Label { Text = "Load Game", HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 40);
        title.AddThemeColorOverride("font_color", UITheme.Gold);
        column.AddChild(title);

        _noteLabel = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(820, 0),
        };
        _noteLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        _noteLabel.AddThemeColorOverride("font_color", UITheme.Warning);
        column.AddChild(_noteLabel);
        ShowNote(Note);

        _slots = MakeVBox(8);
        column.AddChild(_slots);

        var back = new Button
        {
            Text = "Back",
            CustomMinimumSize = new Vector2(200, 44),
            SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
        };
        back.AddThemeFontSizeOverride("font_size", UITheme.FontSizeMedium);
        UITheme.ApplyButtonStyle(back, isPrimary: false);
        back.Pressed += OnBack;
        column.AddChild(back);

        RefreshSlots();
        // Focus off the title's buttons underneath, so a stray Enter cannot press Continue.
        back.GrabFocus();
    }

    private void ShowNote(string text)
    {
        _noteLabel.Text = text ?? "";
        _noteLabel.Visible = !string.IsNullOrEmpty(text);
    }

    private void OnBack()
    {
        if (_changed)
        {
            GetTree().ReloadCurrentScene();
            return;
        }
        QueueFree();
    }

    // ── The slots (the Guild tab's list, moved) ──────────────────────────

    private void RefreshSlots()
    {
        foreach (var child in _slots.GetChildren())
            child.QueueFree();

        foreach (var slot in SaveManager.GetAllSlotInfo())
        {
            bool isActive = SaveManager.ActiveSave != null && slot.Slot == SaveManager.ActiveSlot;

            var card = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            card.AddThemeStyleboxOverride("panel", new StyleBoxFlat
            {
                BgColor = isActive ? new Color(0.10f, 0.18f, 0.10f, 0.9f) : UITheme.BgRaised,
                BorderColor = isActive ? UITheme.Success : UITheme.NeutralDim,
                BorderWidthTop = 1,
                BorderWidthBottom = 1,
                BorderWidthLeft = isActive ? 3 : 1,
                BorderWidthRight = 1,
                CornerRadiusTopLeft = 5,
                CornerRadiusTopRight = 5,
                CornerRadiusBottomLeft = 5,
                CornerRadiusBottomRight = 5,
            });

            var cardMargin = new MarginContainer();
            cardMargin.AddThemeConstantOverride("margin_left", 16);
            cardMargin.AddThemeConstantOverride("margin_right", 16);
            cardMargin.AddThemeConstantOverride("margin_top", 10);
            cardMargin.AddThemeConstantOverride("margin_bottom", 10);
            card.AddChild(cardMargin);

            var cardRow = new HBoxContainer();
            cardRow.AddThemeConstantOverride("separation", 16);
            cardMargin.AddChild(cardRow);

            var infoCol = MakeVBox(4);
            infoCol.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            cardRow.AddChild(infoCol);

            if (slot.IsEmpty)
            {
                var emptyLabel = new Label { Text = $"Slot {slot.Slot + 1}: empty" };
                emptyLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
                emptyLabel.AddThemeColorOverride("font_color", UITheme.CampusSubtleText);
                infoCol.AddChild(emptyLabel);

                var hint = new Label { Text = "Found a new guild here." };
                hint.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
                hint.AddThemeColorOverride("font_color", UITheme.TextDim);
                infoCol.AddChild(hint);
            }
            else
            {
                var nameRow = new HBoxContainer();
                nameRow.AddThemeConstantOverride("separation", 10);
                infoCol.AddChild(nameRow);

                var slotNum = new Label { Text = $"[{slot.Slot + 1}]" };
                slotNum.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
                slotNum.AddThemeColorOverride("font_color", UITheme.CampusSubtleText);
                nameRow.AddChild(slotNum);

                var nameLabel = new Label { Text = slot.GuildName };
                nameLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
                nameLabel.AddThemeColorOverride("font_color", isActive ? Colors.White : UITheme.TextPrimary);
                nameRow.AddChild(nameLabel);

                var schoolBadge = new Label { Text = slot.School };
                schoolBadge.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
                schoolBadge.AddThemeColorOverride("font_color", UITheme.TextSecondary);
                nameRow.AddChild(schoolBadge);

                if (isActive)
                {
                    var activeBadge = new Label { Text = "In play" };
                    activeBadge.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
                    activeBadge.AddThemeColorOverride("font_color", UITheme.Success);
                    nameRow.AddChild(activeBadge);
                }

                var statsRow = new HBoxContainer();
                statsRow.AddThemeConstantOverride("separation", 20);
                infoCol.AddChild(statsRow);
                void AddMiniStat(string label, string value)
                {
                    var lbl = new Label { Text = $"{label}  {value}" };
                    lbl.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
                    lbl.AddThemeColorOverride("font_color", UITheme.CampusSubtleText);
                    statsRow.AddChild(lbl);
                }
                AddMiniStat("Gold:", $"{slot.Gold}");
                AddMiniStat("Runs:", $"{slot.TotalRuns}");
                if (!string.IsNullOrEmpty(slot.LastPlayed) && slot.LastPlayed.Length >= 10)
                    AddMiniStat("Last played:", slot.LastPlayed[..10]);
            }

            var btnCol = MakeVBox(4);
            btnCol.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
            cardRow.AddChild(btnCol);

            int capturedSlot = slot.Slot;
            string capturedName = slot.GuildName;
            bool isEmpty = slot.IsEmpty;

            var mainBtn = new Button
            {
                Text = isEmpty ? "New Game" : "Load",
                CustomMinimumSize = new Vector2(110, 34),
            };
            mainBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
            UITheme.ApplyButtonStyle(mainBtn, isPrimary: !isEmpty);   // gold for a save you can load, plain for an empty slot
            mainBtn.Pressed += () =>
            {
                if (isEmpty)
                    StartNewGame(GetTree(), capturedSlot);
                else
                    LoadSlot(capturedSlot);
            };
            btnCol.AddChild(mainBtn);

            if (!isEmpty)
            {
                var delBtn = new Button { Text = "Delete", CustomMinimumSize = new Vector2(110, 28) };
                delBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
                UITheme.ApplyButtonStyle(delBtn, isPrimary: false);
                delBtn.AddThemeColorOverride("font_color", UITheme.Danger);
                delBtn.Pressed += () => ConfirmDelete(capturedSlot, capturedName);
                btnCol.AddChild(delBtn);
            }

            _slots.AddChild(card);
        }
    }

    /// <summary>A new guild in this slot, founded on the New Game screen.</summary>
    public static void StartNewGame(SceneTree tree, int slot)
    {
        ForgetGuildSession();
        PlayerSession.PendingNewGameSlot = slot;
        tree.ChangeSceneToFile(NewGameScenePath);
    }

    /// <summary>Session state that belongs to the guild in play, dropped when another guild is
    /// loaded, founded or deleted, because Return to Title keeps the process and its statics
    /// alive. A reading that ended must not carry over: the next guild's Gatehouse would open
    /// the new reading's picker, and beginning a reading there archives that guild's own.</summary>
    private static void ForgetGuildSession()
    {
        PlayerSession.CycleEndedByConjunction = false;
    }

    /// <summary>Load a slot and go into the city, as Continue does.</summary>
    private void LoadSlot(int slot)
    {
        bool otherGuild = slot != SaveManager.ActiveSlot;
        if (!SaveManager.Load(slot))
        {
            ShowNote($"Slot {slot + 1} could not be loaded.");
            return;
        }
        if (otherGuild)
            ForgetGuildSession();
        if (Enum.TryParse<CardSchool>(SaveManager.ActiveSave?.SelectedSchool, out var school))
            PlayerSession.SelectedSchool = school;
        PlayerSession.StartInCityOnOpen = true;
        SceneTransition.Quiet(GetTree(), StrategicScenePath, 0.9f);   // hides the city settling in
    }

    /// <summary>Delete only after a yes: the Guild tab deleted on the first click.</summary>
    private void ConfirmDelete(int slot, string guildName)
    {
        var dlg = new ConfirmationDialog
        {
            Title = "Delete this guild?",
            DialogText = $"{guildName} (slot {slot + 1}) and everything it has done are erased.\n"
                       + "This cannot be undone.",
            OkButtonText = "Delete",
            CancelButtonText = "Keep it",
        };
        dlg.Confirmed += () =>
        {
            dlg.QueueFree();
            if (slot == SaveManager.ActiveSlot)
                ForgetGuildSession();
            SaveManager.DeleteSlot(slot);
            _changed = true;
            ShowNote("");
            RefreshSlots();
        };
        dlg.Canceled += () => dlg.QueueFree();
        AddChild(dlg);
        dlg.PopupCentered();
    }
}
