import sys, os
root = sys.argv[1]

def edit(rel, pairs):
    p = os.path.join(root, rel)
    s = open(p, encoding='utf-8').read()
    for old, new in pairs:
        n = s.count(old)
        assert n == 1, f"{rel}: expected 1 match, got {n} for:\n{old}"
        s = s.replace(old, new)
    open(p, 'w', encoding='utf-8').write(s)
    print("ok", rel)

edit('Scripts/UI/DeckEditorUi.cs', [
# Title says which deck is being edited.
(
'''            Text = "Manage Deck",
''',
'''            Text = PlayerDeckSave.UseDebugDeck ? "Manage Debug Deck" : "Manage Deck",
'''),
# Stash: owned rows, then (debug only) the whole library.
(
'''        if (stashed.Count == 0)
        {
            _stashList.AddChild(MakeStub(string.IsNullOrEmpty(_stashSearch)
                ? "Stash is empty." : "No cards match."));
            return;
        }

        foreach (var owned in stashed)
            _stashList.AddChild(BuildRow(owned, save, isActive: false));
    }
''',
'''        if (stashed.Count == 0)
            _stashList.AddChild(MakeStub(string.IsNullOrEmpty(_stashSearch)
                ? "Stash is empty." : "No cards match."));
        else
            foreach (var owned in stashed)
                _stashList.AddChild(BuildRow(owned, save, isActive: false));

        // Debug deck only: every card in the library can be added, so new and
        // reworded cards can be tested without earning them first.
        if (PlayerDeckSave.UseDebugDeck)
            AppendDebugLibrary(save);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Debug deck: add any card from the library
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Lists every card blueprint under the stash, filtered by the same search box
    /// (matches name, id or school). "+ Deck" mints a fresh copy into the DEBUG
    /// collection and slots it (free, while there is room); "+ Stash" only mints it.
    /// Only shown while <see cref="PlayerDeckSave.UseDebugDeck"/> is set, so the real
    /// collection, the real deck and the eternal "known cards" list are never touched.
    /// </summary>
    private void AppendDebugLibrary(GuildSaveData save)
    {
        _stashList.AddChild(new HSeparator());
        _stashList.AddChild(MakeInfoLabel("Library (debug): add any card", UITheme.Warning));

        string q = _stashSearch ?? "";
        var matches = CardDatabase.Blueprints
            .Where(bp => bp != null && (q.Length == 0
                || DebugCardName(bp).Contains(q, StringComparison.OrdinalIgnoreCase)
                || (bp.Id ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)
                || bp.School.ToString().Contains(q, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(bp => bp.School.ToString())
            .ThenBy(bp => DebugCardName(bp))
            .ToList();

        if (matches.Count == 0)
        {
            _stashList.AddChild(MakeStub("No library cards match."));
            return;
        }

        bool deckFull = (save.PlayerDeck.ActiveDeckInstanceIds?.Count ?? 0) >= PlayerDeckSave.MaxDeckSize;
        foreach (var bp in matches)
        {
            var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Pass };
            row.AddThemeConstantOverride("separation", 4);

            var name = new Label
            {
                Text = $"{DebugCardName(bp)}  ·  {bp.School}",
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                ClipText = true,
                MouseFilter = MouseFilterEnum.Pass,
            };
            name.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
            name.AddThemeColorOverride("font_color", UITheme.TextSecondary);
            row.AddChild(name);

            var toDeck = new Button { Text = "+ Deck", Disabled = deckFull, TooltipText = deckFull ? "The deck is full." : "Add a copy and slot it." };
            toDeck.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
            UITheme.ApplyButtonStyle(toDeck, isPrimary: true);
            toDeck.Pressed += () => MintDebugCard(bp, slot: true);
            row.AddChild(toDeck);

            var toStash = new Button { Text = "+ Stash", TooltipText = "Add a copy to the stash." };
            toStash.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
            UITheme.ApplyButtonStyle(toStash, isPrimary: false);
            toStash.Pressed += () => MintDebugCard(bp, slot: false);
            row.AddChild(toStash);

            row.MouseEntered += () => ShowPreview(bp, null);
            name.MouseEntered += () => ShowPreview(bp, null);
            _stashList.AddChild(row);
        }
    }

    private static string DebugCardName(CardBlueprint bp)
        => bp?.Prebuilt?.CardName ?? bp?.Id ?? "";

    /// <summary>Mints a fresh, unupgraded copy of <paramref name="bp"/> into the debug
    /// collection, and slots it when asked and there is room. Debug only.</summary>
    private void MintDebugCard(CardBlueprint bp, bool slot)
    {
        var save = SaveManager.ActiveSave;
        if (save?.PlayerDeck == null || bp == null || !PlayerDeckSave.UseDebugDeck)
            return;

        var owned = new OwnedCard
        {
            BlueprintId = bp.Id,
            InstanceId = Guid.NewGuid().ToString("N"),
            Grafts = new List<string>(),
            IsStarter = false,
        };
        save.PlayerDeck.Cards.Add(owned);   // routes to DebugCards while UseDebugDeck is set

        bool slotted = slot && PlayerDeckService.SlotCard(save, owned.InstanceId);
        GD.Print($"[DeckEditor] Debug: minted '{bp.Id}'{(slotted ? " into the deck" : " into the stash")}.");
        SaveManager.Save();
        Refresh();
    }
'''),
# Slotting into the debug deck is free.
(
'''            // Slotting costs gold
            int cost = PlayerSession.CardSlotCost;
''',
'''            // Slotting costs gold (never for the debug deck: it is a test bench)
            int cost = PlayerDeckSave.UseDebugDeck ? 0 : PlayerSession.CardSlotCost;
'''),
(
'''        // Slotting costs gold; unslotting is free
        int slotCost = PlayerSession.CardSlotCost;
''',
'''        // Slotting costs gold; unslotting is free (and so is everything on the debug deck)
        int slotCost = PlayerDeckSave.UseDebugDeck ? 0 : PlayerSession.CardSlotCost;
'''),
])
