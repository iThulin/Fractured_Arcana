using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// CombatManager.CardChoice.cs  (partial of CombatManager)
//
// Purpose:        Services GameState.OnCardChoiceRequested — the
//                 post-cast player-choice seam. Builds a modal card
//                 picker, waits for the player, and fires the
//                 request's continuation.
// Layer:          System (combat UI glue)
// Collaborators:  CardChoice.cs (the request), RulesManager.cs
//                 (Resolver publishes), CompositeEffects.cs
//                 (SequenceEffect chains), UITheme.cs
// See:            CardChoice.cs for why this is a continuation and
//                 not an await.
// ============================================================

public partial class CombatManager
{
    // Exactly one picker on screen at a time. A resolution CAN produce two requests
    // (two scries in one sequence), so the extras queue rather than overwrite — a
    // dropped request would strand the cards its effect held out of the deck.
    private readonly List<CardChoiceRequest> _choiceQueue = new();
    private CanvasLayer _choiceLayer;
    private CardChoiceRequest _activeChoice;
    private readonly List<Card> _choiceSelected = new();
    private Button _choiceConfirmBtn;
    private readonly Dictionary<Card, Panel> _choiceCardPanels = new();

    /// <summary>Render scale for the real CardUi in the picker. 0.85 keeps a five-card
    /// scry inside a 1920-wide viewport with the panel margins.</summary>
    private const float ChoiceCardScale = 0.85f;

    /// <summary>Wired to GameState.OnCardChoiceRequested at combat start.</summary>
    private void OnCardChoiceRequested(CardChoiceRequest req)
    {
        if (req == null)
            return;

        // No decision to make: the player would be picking every candidate. Resolve it
        // silently. A modal with one legal answer is a click the player cannot act on,
        // which is the same rule R3's auto-pass applies to priority windows.
        if (req.IsDegenerate)
        {
            GD.Print($"[{req.Source}] no choice to make ({req.Candidates?.Count ?? 0} card(s), " +
                     $"pick {req.PickCount}) — taking them all.");
            req.Complete(req.DefaultPick());
            return;
        }

        _choiceQueue.Add(req);
        if (_activeChoice == null)
            ShowNextChoice();
    }

    private void ShowNextChoice()
    {
        if (_choiceQueue.Count == 0)
        { TeardownChoiceUi(); return; }

        _activeChoice = _choiceQueue[0];
        _choiceQueue.RemoveAt(0);
        _choiceSelected.Clear();
        _choiceCardPanels.Clear();

        BuildChoiceUi(_activeChoice);
    }

    private void BuildChoiceUi(CardChoiceRequest req)
    {
        TeardownChoiceUi();

        _choiceLayer = new CanvasLayer { Layer = 90, Name = "CardChoiceLayer" };
        AddChild(_choiceLayer);

        // Full-screen scrim. MouseFilter.Stop is what makes this modal: every click
        // that is not on the picker is swallowed here, so the player cannot cast,
        // move or end the turn with a question outstanding.
        var scrim = new ColorRect
        {
            Color = UITheme.BgOverlay,
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        scrim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _choiceLayer.AddChild(scrim);

        // CenterContainer, not LayoutPreset.Center. The preset puts a control's
        // TOP-LEFT at the screen centre, so the panel hung down and to the right of the
        // middle instead of sitting in it. A CenterContainer stretched to full rect
        // centres its child by size, which is what "centred" has to mean for a panel
        // whose width depends on how many cards are in it.
        var centre = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        centre.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        scrim.AddChild(centre);

        var panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop };
        var pstyle = new StyleBoxFlat { BgColor = UITheme.BgBase, BorderColor = UITheme.Gold };
        pstyle.SetCornerRadiusAll(8);
        pstyle.SetBorderWidthAll(2);
        pstyle.SetContentMarginAll(18);
        panel.AddThemeStyleboxOverride("panel", pstyle);
        centre.AddChild(panel);

        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 10);
        panel.AddChild(col);

        var title = new Label { Text = req.Title, Modulate = UITheme.Gold };
        title.AddThemeFontSizeOverride("font_size", UITheme.FontSizeMedium);
        col.AddChild(title);

        var prompt = new Label { Text = req.Prompt, Modulate = UITheme.TextSecondary };
        prompt.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
        col.AddChild(prompt);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        col.AddChild(row);

        foreach (var card in req.Candidates)
        {
            if (card == null)
                continue;
            row.AddChild(BuildChoiceCard(card));
        }

        _choiceConfirmBtn = new Button { Text = ConfirmLabel(req), Disabled = true };
        col.AddChild(_choiceConfirmBtn);
        _choiceConfirmBtn.Pressed += OnChoiceConfirmed;

        string opened = $"[{req.Source}] {req.Prompt}";
        GD.Print(opened);
        combatUI?.AppendActionLog(opened);
    }

    private string ConfirmLabel(CardChoiceRequest req)
        => $"Confirm ({_choiceSelected.Count}/{req.PickCount})";

    /// <summary>One candidate, rendered as the REAL card.
    ///
    /// Follows CardLibraryUi's preview idiom exactly, because it is the same problem:
    /// a CardUi outside the hand. Instantiate, size, kill its mouse handling, then call
    /// <c>SetStaticDisplay</c> on a deferred zero-timer — deferred because CardUi._Ready
    /// parks the card off-screen at alpha 0 for the draw-in tween, and SetStaticDisplay
    /// has to run AFTER that to undo it.
    ///
    /// The card's own input is disabled outright rather than merely ignored: a live
    /// CardUi is draggable, and a card the player could drag out of a modal and onto the
    /// board would cast a spell they were only supposed to be looking at. Selection is a
    /// flat Button laid over the top instead.
    ///
    /// Falls back to a text block if no CardUi scene is available (deckUiManager absent,
    /// e.g. a martial-only party) — a picker that renders nothing would be a dead end.</summary>
    private Control BuildChoiceCard(Card card)
    {
        const float Pad = 10f;
        float w = UITheme.LibraryCardWidth * ChoiceCardScale;
        float h = UITheme.LibraryCardHeight * ChoiceCardScale;

        var holder = new Panel { CustomMinimumSize = new Vector2(w + Pad * 2, h + Pad * 2) };
        ApplyChoiceCardStyle(holder, selected: false);
        _choiceCardPanels[card] = holder;

        var scene = deckUiManager?.CardUIPackedScene;
        if (scene != null && card.TopHalf != null)
        {
            var frame = new Control
            {
                ClipContents = true,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                Position = new Vector2(Pad, Pad),
                CustomMinimumSize = new Vector2(w, h),
            };
            frame.Size = new Vector2(w, h);
            holder.AddChild(frame);

            var cardUi = scene.Instantiate<CardUi>();
            frame.AddChild(cardUi);
            cardUi.SetCard(card.TopHalf, card.BottomHalf);
            cardUi.OffsetRight = UITheme.LibraryCardWidth;
            cardUi.OffsetBottom = UITheme.LibraryCardHeight;
            cardUi.Scale = new Vector2(ChoiceCardScale, ChoiceCardScale);
            cardUi.PivotOffset = Vector2.Zero;
            cardUi.Position = Vector2.Zero;
            cardUi.Rotation = 0f;
            cardUi.Modulate = Colors.White;
            DisableMouseRecursive(cardUi);

            var captured = cardUi;
            GetTree().CreateTimer(0.0).Timeout += () =>
            {
                if (IsInstanceValid(captured))
                    captured.SetStaticDisplay(ChoiceCardScale);
            };
        }
        else
        {
            var box = new VBoxContainer { Position = new Vector2(Pad, Pad) };
            box.AddThemeConstantOverride("separation", 4);
            holder.AddChild(box);
            var name = new Label { Text = card.CardName ?? "(card)", Modulate = UITheme.TextPrimary };
            name.AddThemeFontSizeOverride("font_size", UITheme.FontSizeNormal);
            name.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            box.AddChild(name);
            AddHalfBlock(box, card.TopHalf);
            AddHalfBlock(box, card.BottomHalf);
        }

        // Selection lives on a flat button over the whole holder — the card beneath has
        // no mouse handling left.
        var btn = new Button { Flat = true, MouseFilter = Control.MouseFilterEnum.Stop };
        btn.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        holder.AddChild(btn);
        btn.Pressed += () => ToggleChoiceCard(card);

        return holder;
    }

    /// <summary>Strips mouse handling from a CardUi and everything under it. Without
    /// this the card keeps its hover-lift, its full-card popout and — the one that
    /// matters — <c>_GetDragData</c>, which would let the player drag a card they are
    /// merely being shown onto the board and cast it.</summary>
    private static void DisableMouseRecursive(Node node)
    {
        if (node is Control c)
            c.MouseFilter = Control.MouseFilterEnum.Ignore;
        foreach (var child in node.GetChildren())
            DisableMouseRecursive(child);
    }

    /// <summary>Text-only rendering of one half. Used ONLY by the no-CardUi-scene
    /// fallback in BuildChoiceCard — when the real card renders, this is dead weight
    /// the player never sees.</summary>
    private void AddHalfBlock(VBoxContainer box, CardHalf half)
    {
        if (half == null)
            return;
        var head = new Label
        {
            Text = $"{half.ManaCost}  {half.Name}",
            Modulate = UITheme.Gold,
        };
        head.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
        box.AddChild(head);

        if (string.IsNullOrEmpty(half.RulesText))
            return;
        var rules = new Label { Text = half.RulesText, Modulate = UITheme.TextDim };
        rules.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall - 1);
        rules.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        box.AddChild(rules);
    }

    private void ApplyChoiceCardStyle(Panel p, bool selected)
    {
        var st = new StyleBoxFlat
        {
            BgColor = selected ? UITheme.BgRaised : UITheme.BgCard,
            BorderColor = selected ? UITheme.Gold : UITheme.Neutral,
        };
        st.SetCornerRadiusAll(6);
        st.SetBorderWidthAll(selected ? 3 : 1);
        p.AddThemeStyleboxOverride("panel", st);
    }

    private void ToggleChoiceCard(Card card)
    {
        if (_activeChoice == null || card == null)
            return;

        if (_choiceSelected.Contains(card))
            _choiceSelected.Remove(card);
        else
        {
            // At the cap, the OLDEST pick drops out rather than the click being
            // rejected. A modal that ignores clicks reads as broken; one that rolls
            // the selection reads as a rule.
            if (_choiceSelected.Count >= _activeChoice.PickCount)
                _choiceSelected.RemoveAt(0);
            _choiceSelected.Add(card);
        }

        foreach (var (c, p) in _choiceCardPanels)
            ApplyChoiceCardStyle(p, _choiceSelected.Contains(c));

        if (_choiceConfirmBtn != null)
        {
            _choiceConfirmBtn.Text = ConfirmLabel(_activeChoice);
            _choiceConfirmBtn.Disabled = _choiceSelected.Count != _activeChoice.PickCount;
        }
    }

    private void OnChoiceConfirmed()
    {
        var req = _activeChoice;
        if (req == null)
            return;
        if (_choiceSelected.Count != req.PickCount)
            return;

        var picked = new List<Card>(_choiceSelected);
        _activeChoice = null;
        TeardownChoiceUi();

        // Complete BEFORE draining the queue: the continuation may itself request a
        // choice, and that request must land behind any already waiting rather than
        // jumping the line.
        req.Complete(picked);

        RefreshDeckCounts();
        deckUiManager?.SafeRefreshUI();

        ShowNextChoice();
    }

    private void TeardownChoiceUi()
    {
        _choiceCardPanels.Clear();
        _choiceConfirmBtn = null;
        if (_choiceLayer != null && IsInstanceValid(_choiceLayer))
            _choiceLayer.QueueFree();
        _choiceLayer = null;
    }
}
