using Godot;
using System.Collections.Generic;

// ============================================================
// ExpeditionManager.ScryDesk.cs
//
// Purpose:        Looking up from the stone. Ruled 2026-09-23 (Magos):
//                 mid-expedition news reaches the wizard by one of three
//                 roads, and the player can look up from the palantir to
//                 the room around it.
//
//                   NOTE       a leaf on the desk. Waits. Read when the
//                              wizard chooses to look up.
//                   MESSENGER  someone comes through the door and stands
//                              at the wizard's side until dismissed. The
//                              scrying carries on around them.
//                   SENDING    mind to mind, THROUGH the stone. The stone
//                              flares, orders are refused for a beat, and
//                              the words arrive. The only channel that
//                              stops the player, because it is the only
//                              one that comes from far enough away to
//                              deserve it.
//
//                 A separate partial file rather than more of the seven
//                 thousand line node, so the desk can be read as one
//                 thing. It shares the class and so every private field.
//
//                 Everything posted goes into CycleState.ScryInbox first
//                 and is presented second, so a note survives aiming the
//                 table elsewhere, a save, and a crash. A note on a desk
//                 does not vanish because the wizard looked away.
// Layer:          System (expedition)
// Collaborators:  ExpeditionWindow3D (the desk, the door, the messenger,
//                 the stone's pulse, the camera), CycleState.ScryInbox,
//                 UITheme (the three accents), HaltNotice (the card)
// See:            docs/session_log_2026-09-21_expedition_v2_step1_data_layer.md
// ============================================================

public partial class ExpeditionManager
{
    private Button _lookUpButton;
    private PanelContainer _deskPanel;
    private VBoxContainer _deskRows;
    private bool _lookingUp;

    /// <summary>How long a sending holds the wizard before the words come.
    /// Long enough to read as an event, short enough that it never reads as a
    /// stall.</summary>
    private const float SendingPauseSeconds = 1.2f;

    // ── Posting ──────────────────────────────────────────────────────────

    /// <summary>Deliver a message. Stored first, presented second.</summary>
    private void PostScryMessage(ScryChannel channel, string title, string body,
                                 string aboutForceId = "")
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        if (cycle == null || string.IsNullOrEmpty(title))
        {
            return;
        }

        cycle.ScryInbox ??= new List<ScryMessage>();
        var msg = new ScryMessage
        {
            Id = $"scry_{cycle.Calendar?.CurrentLunation ?? 0}_{cycle.ScryInbox.Count}_{Time.GetTicksMsec()}",
            Channel = channel,
            Title = title,
            Body = body ?? "",
            Lunation = cycle.Calendar?.CurrentLunation ?? 0,
            Read = false,
            AboutForceId = aboutForceId ?? "",
        };
        cycle.ScryInbox.Add(msg);
        SaveManager.MarkDirty();
        LogRun("scry_" + channel.ToString().ToLowerInvariant(), title);

        switch (channel)
        {
            case ScryChannel.Note:
                PresentNote();
                break;
            case ScryChannel.Messenger:
                PresentMessenger(msg);
                break;
            case ScryChannel.Sending:
                PresentSending(msg);
                break;
        }
    }

    private int UnreadNotes()
    {
        var inbox = SaveManager.ActiveSave?.Cycle?.ScryInbox;
        if (inbox == null)
        {
            return 0;
        }
        int n = 0;
        foreach (var m in inbox)
        {
            if (m != null && !m.Read && m.Channel == ScryChannel.Note)
            {
                n++;
            }
        }
        return n;
    }

    // ── The three presentations ──────────────────────────────────────────

    /// <summary>A leaf lands on the desk. Nothing else happens: the whole
    /// point of a note is that it waits.</summary>
    private void PresentNote()
    {
        int unread = UnreadNotes();
        _window3D?.SetDeskNotes(unread);
        RefreshLookUpButton();
        ShowInfo("A note is left on the desk.");
    }

    /// <summary>Someone comes in. The card waits for them to arrive, because a
    /// message read before its bearer is in the room is a note with legs.</summary>
    private void PresentMessenger(ScryMessage msg)
    {
        if (_window3D == null)
        {
            ShowScryCard(msg, onDismiss: null);
            return;
        }
        ShowInfo("Someone is at the door.");
        _window3D.EnterMessenger(() =>
        {
            ShowScryCard(msg, onDismiss: () => _window3D?.DismissMessenger());
        });
    }

    /// <summary>The stone flares and the wizard is held for a beat. Orders
    /// are refused while it happens: a sending is the one road that comes
    /// from far enough away to earn the interruption.</summary>
    private void PresentSending(ScryMessage msg, System.Action onDone = null)
    {
        if (_window3D != null)
        {
            _window3D.AcceptInput = false;
            _window3D.PulseStone(SendingPauseSeconds);
        }
        ShowInfo("The stone clouds. Something reaches for you through it.");

        if (!IsInsideTree())
        {
            ShowScryCard(msg, onDismiss: onDone);
            return;
        }
        GetTree().CreateTimer(SendingPauseSeconds).Timeout += () =>
        {
            if (!GodotObject.IsInstanceValid(this))
            {
                return;
            }
            ShowScryCard(msg, onDismiss: () =>
            {
                // Orders come back only if nothing ELSE is holding them: a
                // sending that lands while the wizard is looking up must not
                // hand the map its clicks back with the desk still in view.
                if (_window3D != null)
                {
                    _window3D.AcceptInput = !_lookingUp && !ExpeditionComplete;
                }
                onDone?.Invoke();
            });
        };
    }

    // ── The card ─────────────────────────────────────────────────────────

    /// <summary>One message, read. Reuses the halt card's frame with the
    /// channel's accent, then marks it read on dismiss. A MODAL dialog for the
    /// words themselves, because these are things the player is meant to
    /// stop and read, unlike a halt reason that must never block the tile
    /// pick sitting under it.</summary>
    private void ShowScryCard(ScryMessage msg, System.Action onDismiss)
    {
        Color accent = msg.Channel switch
        {
            ScryChannel.Messenger => UITheme.ScryMessengerAccent,
            ScryChannel.Sending => UITheme.ScrySendingAccent,
            _ => UITheme.ScryNoteAccent,
        };
        string road = msg.Channel switch
        {
            ScryChannel.Messenger => "A messenger says",
            ScryChannel.Sending => "A sending, mind to mind",
            _ => "A note on the desk",
        };

        var dlg = new AcceptDialog
        {
            Title = road,
            OkButtonText = msg.Channel == ScryChannel.Messenger ? "Dismiss them" : "Understood",
            MinSize = new Vector2I(520, 260),
        };
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);
        dlg.AddChild(box);

        var title = new Label { Text = msg.Title, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        title.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize + 2);
        title.AddThemeColorOverride("font_color", accent);
        box.AddChild(title);

        var body = new Label { Text = msg.Body, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        body.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize - 2);
        body.AddThemeColorOverride("font_color", UITheme.TextPrimary);
        box.AddChild(body);

        void Done()
        {
            msg.Read = true;
            SaveManager.MarkDirty();
            dlg.QueueFree();
            onDismiss?.Invoke();
            _window3D?.SetDeskNotes(UnreadNotes());
            RefreshLookUpButton();
            if (_lookingUp)
            {
                RebuildDeskPanel();
            }
        }
        dlg.Confirmed += Done;
        dlg.Canceled += Done;
        AddChild(dlg);
        dlg.PopupCentered();
    }

    // ── Looking up ───────────────────────────────────────────────────────

    /// <summary>The verb. Up: the camera lifts, the desk panel lists what is
    /// waiting. Down: back to the stone. Orders are refused while looking up,
    /// because a tile click with the map at the bottom of the frame is a
    /// misclick waiting to happen.</summary>
    private void OnLookUpPressed()
    {
        if (_window3D == null || ExpeditionComplete)
        {
            return;
        }
        if (_striding)
        {
            ShowInfo("Not while the march is running. Halt first.");
            return;
        }

        _lookingUp = !_lookingUp;
        if (_lookingUp)
        {
            _window3D.AcceptInput = false;
            _window3D.LookUp();
            RebuildDeskPanel();
            if (_deskPanel != null)
            {
                _deskPanel.Visible = true;
            }
        }
        else
        {
            _window3D.LookDown();
            _window3D.AcceptInput = true;
            if (_deskPanel != null)
            {
                _deskPanel.Visible = false;
            }
        }
        RefreshLookUpButton();
    }

    private void RefreshLookUpButton()
    {
        if (_lookUpButton == null)
        {
            return;
        }
        int unread = UnreadNotes();
        _lookUpButton.Visible = !ExpeditionComplete;
        _lookUpButton.Text = _lookingUp
            ? "Look Down"
            : (unread > 0 ? $"Look Up ({unread})" : "Look Up");
        _lookUpButton.TooltipText = _lookingUp
            ? "Return your gaze to the stone."
            : (unread > 0
                ? $"{unread} note(s) wait on the desk."
                : "Lift your gaze from the stone to the chamber.");
    }

    /// <summary>The desk, as a list. Unread first, then what has been read this
    /// cycle in case the wizard wants it again. Built on demand, so the cost of
    /// a long inbox is paid only when somebody looks at it.</summary>
    private void RebuildDeskPanel()
    {
        if (_deskRows == null)
        {
            return;
        }
        foreach (var ch in _deskRows.GetChildren())
        {
            _deskRows.RemoveChild(ch);
            ch.QueueFree();
        }

        var inbox = SaveManager.ActiveSave?.Cycle?.ScryInbox;
        var head = new Label { Text = "THE DESK" };
        head.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize - 4);
        head.AddThemeColorOverride("font_color", UITheme.TextDim);
        _deskRows.AddChild(head);

        if (inbox == null || inbox.Count == 0)
        {
            var none = new Label { Text = "Nothing waits here." };
            none.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
            none.AddThemeColorOverride("font_color", UITheme.TextSecondary);
            _deskRows.AddChild(none);
            return;
        }

        // Newest first, unread above read.
        var sorted = new List<ScryMessage>(inbox);
        sorted.Sort((a, b) =>
        {
            int r = a.Read.CompareTo(b.Read);
            return r != 0 ? r : b.Lunation.CompareTo(a.Lunation);
        });

        int shown = 0;
        foreach (var m in sorted)
        {
            if (m == null || shown >= 12)
            {
                break;
            }
            shown++;
            var msg = m;
            var btn = new Button
            {
                Text = (m.Read ? "   " : "●  ") + m.Title,
                CustomMinimumSize = new Vector2(300, 32),
                Alignment = HorizontalAlignment.Left,
            };
            btn.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
            UITheme.ApplyButtonStyle(btn, isPrimary: !m.Read);
            btn.Pressed += () => ShowScryCard(msg, onDismiss: null);
            _deskRows.AddChild(btn);
        }
    }

    /// <summary>Build the Look Up control and the desk panel. Called from the
    /// HUD build beside the other order controls.</summary>
    private void BuildScryDeskHud()
    {
        _lookUpButton = new Button
        {
            Text = "Look Up",
            AnchorLeft = 1f,
            AnchorTop = 0f,
            AnchorRight = 1f,
            AnchorBottom = 0f,
            GrowHorizontal = Control.GrowDirection.Begin,
            OffsetLeft = -150,
            OffsetRight = -12,
            OffsetTop = 396 + HudManager.BarHeight,   // row nine: Make Camp took 348 (2026-09-23)
            OffsetBottom = 436 + HudManager.BarHeight,
        };
        _lookUpButton.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize);
        UITheme.ApplyButtonStyle(_lookUpButton, isPrimary: false);
        _lookUpButton.Pressed += OnLookUpPressed;
        _hudCanvas.AddChild(_lookUpButton);
        _uiHoverBlockers.Add(_lookUpButton);

        // The desk panel: right side, under the button column, only while
        // looking up.
        _deskPanel = new PanelContainer
        {
            Visible = false,
            AnchorLeft = 1f,
            AnchorTop = 0f,
            AnchorRight = 1f,
            AnchorBottom = 0f,
            GrowHorizontal = Control.GrowDirection.Begin,
            OffsetLeft = -340,
            OffsetRight = -12,
            OffsetTop = 448 + HudManager.BarHeight,
            OffsetBottom = 448 + 400 + HudManager.BarHeight,
        };
        _deskPanel.AddThemeStyleboxOverride("panel",
            UITheme.MakePanelStyle(UITheme.BgRaised, UITheme.ScryNoteAccent));
        _hudCanvas.AddChild(_deskPanel);
        _uiHoverBlockers.Add(_deskPanel);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 10);
        margin.AddThemeConstantOverride("margin_right", 10);
        margin.AddThemeConstantOverride("margin_top", 8);
        margin.AddThemeConstantOverride("margin_bottom", 8);
        _deskPanel.AddChild(margin);

        var scroll = new ScrollContainer();
        margin.AddChild(scroll);
        _deskRows = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _deskRows.AddThemeConstantOverride("separation", 4);
        scroll.AddChild(_deskRows);

        RefreshLookUpButton();
    }

    /// <summary>Lay whatever was already waiting when the run began: notes that
    /// arrived while the table was aimed elsewhere, or before a save. Then
    /// deliver any sendings that were written while nobody sat at the stone,
    /// because a sending that waited politely in a list was never a sending.</summary>
    private void SeedDeskFromInbox()
    {
        _window3D?.SetDeskNotes(UnreadNotes());
        RefreshLookUpButton();
        CallDeferred(nameof(DeliverPendingSendings));
    }

    /// <summary>Play every unread SENDING in the inbox, one after another, each
    /// with the stone's flare and pause. These are what the world did to a
    /// force the wizard left in the field (SortieThreats), and anything else
    /// the strategic layer wrote into the inbox while no expedition was up.
    ///
    /// <para>Sequential, not simultaneous: three raids delivered as three
    /// stacked dialogs read as a bug. Each waits for the last to be dismissed.
    /// Deferred a frame past SeedDeskFromInbox so the 3D window exists to carry
    /// the pulse.</para></summary>
    private void DeliverPendingSendings()
    {
        var inbox = SaveManager.ActiveSave?.Cycle?.ScryInbox;
        if (inbox == null || ExpeditionComplete)
        {
            return;
        }

        ScryMessage next = null;
        foreach (var m in inbox)
        {
            if (m != null && !m.Read && m.Channel == ScryChannel.Sending)
            {
                next = m;
                break;
            }
        }
        if (next == null)
        {
            return;
        }

        // PresentSending marks it read on dismiss; chain the next one from
        // there so they arrive one at a time.
        PresentSending(next, onDone: DeliverPendingSendings);
    }
}
