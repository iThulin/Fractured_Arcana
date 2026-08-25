using Godot;
using static CampusUi;

// ============================================================
// CampusArmoryPanel.cs
//
// Purpose:        The Armory tab: pick a unit, see its equipped
//                 loadout, and equip/swap from the unequipped pool.
// Layer:          UI
// Collaborators:  CampusPanel.cs (base), CampusContext.cs,
//                 EquipmentLoadout.cs (save.Armory does all mutation),
//                 ItemDatabase.cs, UITheme.RarityColor
// See:            docs/campus_tab_extraction_v1.md (Phase 2)
// ============================================================

/// <summary>Armory. Every mutation goes through <c>save.Armory</c>
/// (<see cref="EquipmentLoadout"/>); this panel renders and dispatches.
///
/// <para><b>Selection state is intentionally sticky across refreshes.</b> <c>_selectedUnitId</c>
/// and <c>_slotFilter</c> survive a <see cref="Refresh"/> because the whole tab rebuilds on
/// every equip, and losing the selected unit mid-outfitting would be maddening. Switching
/// units DOES reset the filter to "All", which is deliberate: the previous unit's filter is
/// rarely the right one for the next.</para>
///
/// <para>This is the panel that most argued for a stateful CampusPanel over a static
/// <c>BuildInto</c> renderer. It holds three pieces of live selection state that a static
/// builder would have had to thread through parameters.</para>
///
/// <para><b>EnsureStarterItems did not move.</b> It seeds save data rather than drawing
/// anything, and is already invoked from the shell's slot-selection path. It stays with the
/// other Ensure* seeding on CampusScreen.</para>
///
/// <para>Extracted from <c>CampusScreen</c> on 2026-08-03, unchanged.</para></summary>
public sealed class CampusArmoryPanel : CampusPanel
{
    private VBoxContainer _container;
    private string _selectedUnitId = null;   // which unit we're equipping
    private string _slotFilter = "All";      // "All", "Weapon", "Armor", "Trinket"

    protected override void OnBuild(ScrollContainer scroll)
    {
        var outer = MakeMargins(20, 16);
        scroll.AddChild(outer);

        _container = MakeVBox(12);
        _container.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        outer.AddChild(_container);

        Refresh();
    }

    public override void Refresh()
    {
        if (_container == null)
            return;

        foreach (Node child in _container.GetChildren())
            child.QueueFree();

        var save = Ctx?.Save;
        if (save == null)
        {
            _container.AddChild(MakeStubLabel("No save loaded."));
            return;
        }

        Ctx.RefreshGold?.Invoke();
        ItemDatabase.LoadAll();

        // ── Unit selector ────────────────────────────────────────────────
        AddSectionHeader(_container, "Equip To");
        BuildUnitSelector(save);

        // ── Currently equipped ───────────────────────────────────────────
        if (_selectedUnitId != null)
        {
            AddSectionHeader(_container, "Equipped");
            BuildEquippedPanel(save);
        }

        // ── Unequipped items ─────────────────────────────────────────────
        AddSectionHeader(_container, "Armory");
        BuildUnequippedPanel(save);

        // ── Scriptorium (rehomed 2026-08-21) ─────────────────────────────
        // Scroll crafting lived on the Expedition tab, but the deploy-flow
        // streamline routes the Gatehouse straight to the launch drawer, so
        // that tab is no longer on the normal path. Scrolls are items; the
        // Armory is their door now. (The Expedition tab keeps a copy for its
        // fallback appearances: known duplication, unify when the Scribe's
        // Tower claims scroll crafting per R8.)
        _container.AddChild(new HSeparator());
        AddSectionHeader(_container, "Scriptorium: Scrolls");
        var scrollHint = new Label
        {
            Text = "A scroll holds one cast of a spell the guild knows, usable by any " +
                   "school, consuming no Essence, spent on use.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        scrollHint.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        scrollHint.Modulate = UITheme.CampusSubtleText;
        _container.AddChild(scrollHint);
        _scriptoriumList = MakeVBox(6);
        _container.AddChild(_scriptoriumList);
        RefreshScriptorium();
    }

    // ── Scriptorium (mirrors CampusExpeditionPanel.RefreshScriptorium) ───

    private VBoxContainer _scriptoriumList;

    /// <summary>S4: rebuild the Scriptorium rows, one per scribable spell
    /// (school innates + learned spells; no Attunements, no Emulate). Narrow
    /// refresh on scribe so the shopping scroll position survives.</summary>
    private void RefreshScriptorium()
    {
        if (_scriptoriumList == null)
            return;
        foreach (var child in _scriptoriumList.GetChildren())
            child.QueueFree();

        var save = Ctx?.Save;
        var grim = save?.Cycle?.Grimoire;
        if (grim == null)
            return;
        OverworldSpellRegistry.EnsureLoaded();

        var scribable = new System.Collections.Generic.List<OverworldSpellDefinition>();
        void AddDef(OverworldSpellDefinition d)
        {
            if (d != null && !d.IsAttunement && d.EffectKey != "emulate" && !scribable.Contains(d))
                scribable.Add(d);
        }
        foreach (var innate in OverworldSpellRegistry.InnatesFor(save.Cycle.SelectedSchool))
            AddDef(innate);
        foreach (var id in grim.KnownSpellIds)
            AddDef(OverworldSpellRegistry.Get(id));
        scribable.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

        if (scribable.Count == 0)
        {
            var none = new Label
            {
                Text = "The guild knows nothing worth scribing yet. Spells are learned " +
                       "afield (lore sites, cordial deals, the dead).",
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            none.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
            none.Modulate = UITheme.CampusSubtleText;
            _scriptoriumList.AddChild(none);
            return;
        }

        foreach (var def in scribable)
        {
            int cost = SpellAcquisition.ScrollGoldCost(def);
            grim.ScrollInventory.TryGetValue(def.Id, out int held);

            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 10);
            _scriptoriumList.AddChild(row);

            var name = new Label
            {
                Text = $"{def.Name}  ·  {def.Magnitude}" + (held > 0 ? $"  ·  ×{held} held" : ""),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                TooltipText = def.Description,
            };
            name.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
            name.AddThemeColorOverride("font_color", UITheme.TextPrimary);
            row.AddChild(name);

            var craftBtn = MakeButton($"Scribe ({cost} g)", 150, 34,
                UITheme.CampusSmallFontSize, isPrimary: false);
            craftBtn.Disabled = save.Gold < cost;
            string id = def.Id; // capture per-iteration
            craftBtn.Pressed += () =>
            {
                var s = Ctx?.Save;
                var g = s?.Cycle?.Grimoire;
                if (s == null || g == null || s.Gold < cost)
                    return;
                s.Gold -= cost;
                g.ScrollInventory[id] = g.ScrollInventory.TryGetValue(id, out int n) ? n + 1 : 1;
                SaveManager.MarkDirty();
                GD.Print($"[Scriptorium] Scribed '{id}' for {cost}g " +
                         $"(held ×{g.ScrollInventory[id]}, gold {s.Gold}).");
                Ctx.RefreshGold?.Invoke();
                RefreshScriptorium();
            };
            row.AddChild(craftBtn);
        }
    }

    // ── Unit selector row ────────────────────────────────────────────────

    private void BuildUnitSelector(GuildSaveData save)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        _container.AddChild(row);

        // Wizard button (always present)
        AddUnitSelectorButton(row, "wizard", "Wizard", UITheme.Violet);

        // Active party companions
        foreach (var companionId in save.ActivePartyCompanionIds)
        {
            var companion = save.Companions.Find(c => c.Id == companionId);
            if (companion == null || companion.IsPermadead)
                continue;

            AddUnitSelectorButton(row, companion.Id, companion.Name, UITheme.Success);
        }
    }

    private void AddUnitSelectorButton(HBoxContainer row, string unitId, string label, Color accentColor)
    {
        bool isSelected = _selectedUnitId == unitId;

        var btn = new Button
        {
            Text = label,
            ToggleMode = true,
            ButtonPressed = isSelected,
            CustomMinimumSize = new Vector2(120, 36),
        };
        btn.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);

        if (isSelected)
            btn.AddThemeColorOverride("font_color", accentColor);

        string captured = unitId;
        btn.Pressed += () =>
        {
            _selectedUnitId = captured;
            _slotFilter = "All"; // reset filter on unit switch
            Refresh();
        };

        row.AddChild(btn);
    }

    // ── Equipped panel ───────────────────────────────────────────────────

    private void BuildEquippedPanel(GuildSaveData save)
    {
        var grid = new GridContainer { Columns = 3 };
        grid.AddThemeConstantOverride("h_separation", UITheme.PaddingNormal);
        grid.AddThemeConstantOverride("v_separation", UITheme.PaddingNormal);
        grid.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _container.AddChild(grid);

        foreach (EquipmentSlot slot in System.Enum.GetValues(typeof(EquipmentSlot)))
        {
            var loadout = save.Armory.GetLoadout(_selectedUnitId);
            var instanceId = loadout.GetSlot(slot);
            var item = instanceId != null ? save.Armory.GetInstance(instanceId) : null;

            var card = BuildItemSlotCard(slot, item, save);
            grid.AddChild(card);
        }
    }

    private Control BuildItemSlotCard(EquipmentSlot slot, ItemInstance item, GuildSaveData save)
    {
        var panel = new PanelContainer();
        panel.CustomMinimumSize = new Vector2(180, 90);

        var style = new StyleBoxFlat
        {
            BgColor = UITheme.SurfaceLight,
            BorderColor = item != null ? UITheme.RarityColor(item.Rarity) : UITheme.Neutral,
            CornerRadiusTopLeft = UITheme.CornerRadius - 1,
            CornerRadiusTopRight = UITheme.CornerRadius - 1,
            CornerRadiusBottomLeft = UITheme.CornerRadius - 1,
            CornerRadiusBottomRight = UITheme.CornerRadius - 1,
            BorderWidthTop = UITheme.BorderWidth - 1,
            BorderWidthBottom = UITheme.BorderWidth - 1,
            BorderWidthLeft = UITheme.BorderWidth - 1,
            BorderWidthRight = UITheme.BorderWidth - 1,
            ContentMarginLeft = UITheme.PaddingNormal + 2,
            ContentMarginRight = UITheme.PaddingNormal + 2,
            ContentMarginTop = UITheme.PaddingNormal,
            ContentMarginBottom = UITheme.PaddingNormal,
        };
        panel.AddThemeStyleboxOverride("panel", style);

        var vbox = MakeVBox(4);
        panel.AddChild(vbox);

        // Slot label
        var slotLbl = new Label { Text = slot.ToString().ToUpper() };
        slotLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        slotLbl.AddThemeColorOverride("font_color", UITheme.TextOnLight);
        vbox.AddChild(slotLbl);

        if (item != null)
        {
            // Item name
            var nameLbl = new Label { Text = item.Name };
            nameLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
            nameLbl.AddThemeColorOverride("font_color", UITheme.RarityColor(item.Rarity));
            nameLbl.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            vbox.AddChild(nameLbl);

            // Stats summary
            var def = ItemDatabase.Get(item.DefinitionId);
            if (def != null)
            {
                var statsLbl = new Label { Text = BuildStatSummary(def) };
                statsLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
                statsLbl.AddThemeColorOverride("font_color", UITheme.TextOnLight);
                statsLbl.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                vbox.AddChild(statsLbl);
            }

            // Unequip button
            var unequipBtn = new Button
            {
                Text = "Unequip",
                CustomMinimumSize = new Vector2(0, 24),
            };
            unequipBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
            EquipmentSlot capturedSlot = slot;
            unequipBtn.Pressed += () =>
            {
                save.Armory.Unequip(_selectedUnitId, capturedSlot);
                SaveManager.Save();
                Refresh();
            };
            vbox.AddChild(unequipBtn);
        }
        else
        {
            var emptyLbl = new Label { Text = "- Empty -" };
            emptyLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
            emptyLbl.AddThemeColorOverride("font_color", UITheme.TextDim);
            vbox.AddChild(emptyLbl);
        }

        return panel;
    }

    // ── Unequipped items list ─────────────────────────────────────────────

    private void BuildUnequippedPanel(GuildSaveData save)
    {
        var allUnequipped = save.Armory.GetUnequipped();

        if (allUnequipped.Count == 0)
        {
            _container.AddChild(MakeStubLabel("All items are equipped."));
            return;
        }

        // ── Filter bar ────────────────────────────────────────────────
        var filterRow = new HBoxContainer();
        filterRow.AddThemeConstantOverride("separation", 4);
        _container.AddChild(filterRow);

        foreach (var filterName in new[] { "All", "Weapon", "Armor", "Trinket" })
        {
            bool isActive = _slotFilter == filterName;
            var filterBtn = new Button
            {
                Text = filterName,
                ToggleMode = true,
                ButtonPressed = isActive,
                CustomMinimumSize = new Vector2(80, 28),
            };
            filterBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
            ApplyTabStyle(filterBtn, isActive);

            string captured = filterName;
            filterBtn.Pressed += () =>
            {
                _slotFilter = captured;
                Refresh();
            };
            filterRow.AddChild(filterBtn);
        }

        // ── Filtered list ─────────────────────────────────────────────
        var filtered = _slotFilter == "All"
            ? allUnequipped
            : allUnequipped.FindAll(i => i.Slot == _slotFilter);

        if (filtered.Count == 0)
        {
            _container.AddChild(MakeStubLabel($"No {_slotFilter} items in armory."));
            return;
        }

        var countLbl = new Label
        {
            Text = _slotFilter == "All"
                ? $"{filtered.Count} items"
                : $"{filtered.Count} {_slotFilter}s",
        };
        countLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        countLbl.AddThemeColorOverride("font_color", UITheme.TextSecondary);
        _container.AddChild(countLbl);

        foreach (var item in filtered)
            _container.AddChild(BuildUnequippedItemRow(item, save));
    }

    private Control BuildUnequippedItemRow(ItemInstance item, GuildSaveData save)
    {

        var panel = new PanelContainer();
        var style = new StyleBoxFlat
        {
            BgColor = UITheme.SurfaceLight,
            BorderColor = UITheme.RarityColor(item.Rarity),
            CornerRadiusTopLeft = UITheme.CornerRadius - 1,
            CornerRadiusTopRight = UITheme.CornerRadius - 1,
            CornerRadiusBottomLeft = UITheme.CornerRadius - 1,
            CornerRadiusBottomRight = UITheme.CornerRadius - 1,
            BorderWidthTop = UITheme.BorderWidth - 1,
            BorderWidthBottom = UITheme.BorderWidth - 1,
            BorderWidthLeft = UITheme.BorderWidth - 1,
            BorderWidthRight = UITheme.BorderWidth - 1,
            ContentMarginLeft = UITheme.PaddingNormal + 2,
            ContentMarginRight = UITheme.PaddingNormal + 2,
            ContentMarginTop = UITheme.PaddingNormal,
            ContentMarginBottom = UITheme.PaddingNormal,
        };
        panel.AddThemeStyleboxOverride("panel", style);
        panel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);
        panel.AddChild(row);

        // Left: name + details
        var info = MakeVBox(2);
        info.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        row.AddChild(info);

        var nameRow = new HBoxContainer();
        nameRow.AddThemeConstantOverride("separation", 8);
        info.AddChild(nameRow);

        var nameLbl = new Label { Text = item.Name };
        nameLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
        nameLbl.AddThemeColorOverride("font_color", UITheme.RarityColor(item.Rarity));
        nameRow.AddChild(nameLbl);

        var slotBadge = new Label { Text = $"[{item.Slot}]" };
        slotBadge.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        slotBadge.AddThemeColorOverride("font_color", UITheme.TextOnLight);
        nameRow.AddChild(slotBadge);

        var classBadge = new Label { Text = $"[{item.UnitClass}]" };
        classBadge.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        classBadge.AddThemeColorOverride("font_color", UITheme.SuccessDim);
        nameRow.AddChild(classBadge);

        var def = ItemDatabase.Get(item.DefinitionId);
        if (def != null)
        {
            var statsLbl = new Label { Text = BuildStatSummary(def) };
            statsLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
            statsLbl.AddThemeColorOverride("font_color", UITheme.TextOnLight);
            info.AddChild(statsLbl);

            if (!string.IsNullOrEmpty(def.Description))
            {
                var descLbl = new Label
                {
                    Text = def.Description,
                    AutowrapMode = TextServer.AutowrapMode.WordSmart,
                };
                descLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
                descLbl.AddThemeColorOverride("font_color", UITheme.TextDim);
                info.AddChild(descLbl);
            }
        }

        // Right: equip button
        if (_selectedUnitId != null && def != null)
        {
            if (System.Enum.TryParse<EquipmentSlot>(item.Slot, true, out var itemSlot))
            {
                var loadout = save.Armory.GetLoadout(_selectedUnitId);
                string currentInstanceId = loadout.GetSlot(itemSlot);

                string btnText = currentInstanceId != null ? "Swap →" : "Equip →";

                var equipBtn = new Button
                {
                    Text = btnText,
                    CustomMinimumSize = new Vector2(90, 32),
                };
                equipBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
                UITheme.ApplyButtonStyle(equipBtn, isPrimary: true);

                string capturedInstId = item.InstanceId;
                equipBtn.Pressed += () =>
                {
                    // Swap: unequip current first, then equip new
                    if (currentInstanceId != null)
                        save.Armory.Unequip(_selectedUnitId, itemSlot);
                    save.Armory.Equip(_selectedUnitId, capturedInstId);
                    SaveManager.Save();
                    Refresh();
                };

                var btnCol = MakeVBox(4);
                btnCol.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;
                row.AddChild(btnCol);
                btnCol.AddChild(equipBtn);
            }
        }

        return panel;
    }

    private string BuildStatSummary(ItemDefinition def)
    {
        var parts = new System.Collections.Generic.List<string>();

        if (def.Stats.MaxHP != 0)
            parts.Add($"+{def.Stats.MaxHP} HP");
        if (def.Stats.MaxMana != 0)
            parts.Add($"+{def.Stats.MaxMana} Mana");
        if (def.Stats.Armor != 0)
            parts.Add($"+{def.Stats.Armor} Armor");
        if (def.Stats.BaseSpeed != 0)
            parts.Add($"+{def.Stats.BaseSpeed} Speed");
        if (def.Stats.AttackDamage != 0)
            parts.Add($"+{def.Stats.AttackDamage} Atk");
        if (def.Stats.AttackRange != 0)
            parts.Add($"+{def.Stats.AttackRange} Range");
        if (def.Stats.SpellDamage != 0)
            parts.Add($"+{def.Stats.SpellDamage} SpellDmg");

        if (def.Passive != "None" && !string.IsNullOrEmpty(def.Passive))
            parts.Add(PassiveLabel(def.Passive, def.PassiveValue));

        return parts.Count > 0 ? string.Join("  ·  ", parts) : "No bonuses";
    }

    private string PassiveLabel(string passive, int value) => passive switch
    {
        "StormSpellCostReduction" => $"Storm spells cost -{value} mana",
        "FireSpellBonusDamage" => $"Fire spells +{value} dmg",
        "StartCombatWithShield" => $"Start with {value} shield",
        "RestoreManaOnTurnStart" => $"Restore {value} mana/turn",
        "FirstCardCostReduction" => $"First card costs -{value} mana",
        "AttackAppliesBleed" => "Attacks apply bleed",
        "BonusDamageAboveHalfHP" => $"+{value} atk above 50% HP",
        "DamageReductionPerHit" => $"Take -{value} dmg per hit",
        _ => passive,
    };
}
