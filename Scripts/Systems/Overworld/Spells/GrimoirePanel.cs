using Godot;
using System.Collections.Generic;

// ============================================================
// GrimoirePanel.cs  (S2, 2026-07-15)
//
// Purpose:        The Grimoire, which is the overworld HUD's spell
//                 panel, bottom-left. Lists the expedition's castable
//                 spells with cost (corrupted-ground surcharge
//                 shown inline), magnitude tint, and a one-line
//                 effect tooltip; a status row shows active timed
//                 effects. Buttons disable with a legible reason
//                 (G5). Procedural build under the standard rules;
//                 colors via UITheme.
//
//                 S2 simplification (noted vs the design doc's
//                 collapse-to-icon-strip): a fixed compact list.
//                 Collapse/expand polish can ride a later pass.
//                 S4: a Scrolls section under the spell list holds
//                 Essence-free casts from GrimoireState.
//                 ScrollInventory, routed through
//                 RequestScrollCast.
//                 S4.1 (user request 2026-07-16): the native
//                 tooltip is gone. Hovering a row opens an
//                 OPAQUE detail card to the panel's right (name,
//                 school · category · magnitude, live cost
//                 breakdown, full description, block reason).
//                 The card is a SIBLING on the HUD canvas (added
//                 deferred, since a PanelContainer would layout-manage
//                 a child), freed in _ExitTree.
// Layer:          UI
// Collaborators:  OverworldSpellManager.cs (data + cast entry),
//                 ExpeditionManager.cs (creates + refreshes),
//                 UITheme.cs (colors)
// See:            overworld_spell_system_v1_1.docx §12
// ============================================================

/// <summary>Bottom-left overworld spell panel. Refresh() rebuilds from the
/// manager's castable list, which is cheap at ≤ a dozen rows.</summary>
public partial class GrimoirePanel : PanelContainer
{
    /// <summary>Layout constants shared by the panel and its detail card.</summary>
    private const float PanelLeft = 12, PanelRight = 292, PanelBottom = -12;
    private const float DetailGap = 8, DetailWidth = 340;

    /// <summary>Cap on the spell list's height, as a fraction of the viewport.
    /// Debug mode exposes all 36 implemented spells, and without a clamp the
    /// panel runs off the top of the screen (user report, 2026-07-16).</summary>
    private const float MaxListViewportShare = 0.55f;

    // ── S5.1 (user request): grouped, collapsible Grimoire ──────────────
    // The LOADOUT (own-school innates + prepared spells) is always visible;
    // everything else groups by §6 Category under collapsible headers that
    // start collapsed. STATIC so open/closed state survives panel rebuilds,
    // combat round-trips, and whole expeditions. "Remember the last state."
    private static readonly System.Collections.Generic.Dictionary<string, bool>
        _sectionOpen = new();

    /// <summary>§6 taxonomy order, which is problem-solving order, not alphabetical.</summary>
    private static readonly string[] CategoryOrder =
    {
        "Traversal", "Divination", "Warding", "Evasion", "Conjuration", "Communion",
    };

    private static bool IsOpen(string key)
        => _sectionOpen.TryGetValue(key, out bool open) && open;

    /// <summary>S5.2 (user request): whole-panel visibility. STATIC so the
    /// state rides through combat/negotiation scene swaps: leave hidden,
    /// return hidden (and vice versa). A small "Grimoire" tab (sibling on
    /// the HUD canvas) is the way back in while the panel is off-screen.</summary>
    private static bool _panelHidden = false;
    private Button _reopenTab;

    private OverworldSpellManager _manager;
    private ScrollContainer _scroll;
    private VBoxContainer _list;

    // ── S4.1: hover detail card (sibling on the HUD canvas) ─────────────
    private PanelContainer _detail;
    private VBoxContainer _detailBox;

    public void Initialize(OverworldSpellManager manager)
    {
        _manager = manager;

        // Bottom-left anchor. S4.2 fix: the rect is managed EXPLICITLY, with the
        // bottom edge pinned at PanelBottom and the top edge set from content in
        // ClampListHeight. (The earlier grow-direction + SetSize approach
        // re-anchored the TOP-left on resize, which laid the panel out
        // DOWNWARD off the bottom of the screen. See the user screenshot bug.)
        AnchorLeft = 0f; AnchorRight = 0f;
        AnchorTop = 1f; AnchorBottom = 1f;
        OffsetLeft = PanelLeft;
        OffsetRight = PanelRight;
        OffsetBottom = PanelBottom;
        OffsetTop = PanelBottom - 80; // placeholder; ClampListHeight sets the real height

        AddThemeStyleboxOverride("panel",
            UITheme.MakePanelStyle(UITheme.OverworldHudBg, UITheme.OverworldHudBorder));

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 10);
        margin.AddThemeConstantOverride("margin_right", 10);
        margin.AddThemeConstantOverride("margin_top", 8);
        margin.AddThemeConstantOverride("margin_bottom", 8);
        AddChild(margin);

        // S4.2: the list lives in a scroller so a long Grimoire (debug mode:
        // all 36) clamps to a fraction of the screen and scrolls instead of
        // running off the top. Height is set per-Refresh in ClampListHeight.
        _scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
        };
        margin.AddChild(_scroll);

        _list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _list.AddThemeConstantOverride("separation", 4);
        _scroll.AddChild(_list);

        Refresh();

        // S4.1/S5.2: the detail card and the reopen tab must be SIBLINGS
        // (this PanelContainer would layout-manage children); the parent
        // exists by now, but defer to stay safe against setup order.
        CallDeferred(nameof(CreateSatellites));
    }

    private void CreateSatellites()
    {
        CreateDetailCard();
        CreateReopenTab();
        ApplyHiddenState(); // honor the state we left the expedition in
    }

    // ── S5.2: hide / reopen ──────────────────────────────────────────────

    private void CreateReopenTab()
    {
        if (_reopenTab != null || GetParent() == null)
            return;
        _reopenTab = new Button
        {
            Name = "GrimoireReopenTab",
            Text = "Grimoire",
            Visible = false,
            AnchorLeft = 0f, AnchorRight = 0f,
            AnchorTop = 1f, AnchorBottom = 1f,
            OffsetLeft = PanelLeft,
            OffsetRight = PanelLeft + 120,
            OffsetTop = PanelBottom - 34,
            OffsetBottom = PanelBottom,
        };
        _reopenTab.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize - 2);
        UITheme.ApplyButtonStyle(_reopenTab, isPrimary: false);
        _reopenTab.AddThemeColorOverride("font_color", UITheme.Gold);
        _reopenTab.Pressed += () => { _panelHidden = false; ApplyHiddenState(); };
        GetParent().AddChild(_reopenTab);
    }

    /// <summary>Show either the panel or its reopen tab, never both.</summary>
    private void ApplyHiddenState()
    {
        Visible = !_panelHidden;
        if (_reopenTab != null)
            _reopenTab.Visible = _panelHidden;
        if (_panelHidden)
            HideDetail(); // no orphaned detail card beside a hidden panel
    }

    // ════════════════════════════════════════════════════════════════════
    // S4.1: hover detail card
    // ════════════════════════════════════════════════════════════════════

    private void CreateDetailCard()
    {
        if (_detail != null || GetParent() == null)
            return;

        _detail = new PanelContainer
        {
            Name = "GrimoireDetailCard",
            Visible = false,
            // Bottom-aligned beside the Grimoire; grows upward like it.
            AnchorLeft = 0f, AnchorRight = 0f,
            AnchorTop = 1f, AnchorBottom = 1f,
            GrowVertical = Control.GrowDirection.Begin,
            OffsetLeft = PanelRight + DetailGap,
            OffsetRight = PanelRight + DetailGap + DetailWidth,
            OffsetBottom = PanelBottom,
            // Read-only card, so never steal clicks from the map beneath.
            MouseFilter = MouseFilterEnum.Ignore,
        };

        // FULLY OPAQUE, which is the whole point. The HUD background color reads
        // through the map at its authored alpha; force A = 1 here.
        var bg = UITheme.OverworldHudBg;
        bg.A = 1f;
        _detail.AddThemeStyleboxOverride("panel",
            UITheme.MakePanelStyle(bg, UITheme.OverworldHudBorder));

        var margin = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
        margin.AddThemeConstantOverride("margin_left", 12);
        margin.AddThemeConstantOverride("margin_right", 12);
        margin.AddThemeConstantOverride("margin_top", 10);
        margin.AddThemeConstantOverride("margin_bottom", 10);
        _detail.AddChild(margin);

        _detailBox = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        _detailBox.AddThemeConstantOverride("separation", 5);
        margin.AddChild(_detailBox);

        GetParent().AddChild(_detail);
    }

    /// <summary>Fill and show the detail card for one spell (or scroll).</summary>
    private void ShowDetail(OverworldSpellDefinition def, bool isScroll, int scrollsHeld)
    {
        if (_detail == null || _detailBox == null || def == null || _manager == null)
            return;

        foreach (var child in _detailBox.GetChildren())
            child.QueueFree();

        Label Add(string text, int sizeDelta, Color color, bool wrap = false)
        {
            var l = new Label
            {
                Text = text,
                AutowrapMode = wrap ? TextServer.AutowrapMode.WordSmart
                                    : TextServer.AutowrapMode.Off,
            };
            if (wrap)
                l.CustomMinimumSize = new Vector2(DetailWidth - 24, 0);
            l.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize + sizeDelta);
            l.AddThemeColorOverride("font_color", color);
            _detailBox.AddChild(l);
            return l;
        }

        // Name + taxonomy line.
        Add(def.Name, +2, MagnitudeColor(def.Magnitude));
        string meta = $"{def.School}  ·  {def.Category}  ·  {def.Magnitude}";
        if (def.IsInnate)
            meta += "  ·  innate";
        if (def.OncePerExpedition)
            meta += "  ·  once per expedition";
        Add(meta, -4, UITheme.TextSecondary);

        // Cost line: the live breakdown, or the scroll note.
        if (isScroll)
        {
            Add($"Scroll ×{scrollsHeld}. No Essence; spent on a successful cast", -2,
                UITheme.EssenceText);
        }
        else if (!def.IsAttunement)
        {
            int tax = _manager.OffCasterTax(def);
            int surcharge = _manager.CorruptionSurcharge();
            string cost = $"Cost: {def.EssenceCost}✦";
            if (tax > 0)
                cost += $"  +{tax} off-school";
            if (surcharge > 0)
                cost += $"  +{surcharge} corrupted ground";
            Add(cost, -2, UITheme.EssenceText);
        }

        _detailBox.AddChild(new HSeparator { MouseFilter = MouseFilterEnum.Ignore });

        // Full description: wrapped, never truncated, never translucent.
        Add(def.Description, -2, UITheme.TextPrimary, wrap: true);

        // S5 (R15, G5): the tier-3 exposure is priced in HP, not Essence, so
        // it must be visible BEFORE the cast, scroll or not.
        string exposure = _manager.ExposureWarning();
        if (exposure != null && !def.IsAttunement)
            Add(exposure, -2, UITheme.OverworldLowResourceWarning, wrap: true);

        // Why it's disabled, when it is.
        string block = _manager.CastBlockReason(def, ignoreEssence: isScroll);
        if (block != null)
            Add(block, -2, UITheme.OverworldLowResourceWarning, wrap: true);

        _detail.Visible = true;
    }

    private void HideDetail()
    {
        if (_detail != null)
            _detail.Visible = false;
    }

    public override void _ExitTree()
    {
        // The card and reopen tab are siblings, not children, so free them explicitly.
        _detail?.QueueFree();
        _detail = null;
        _detailBox = null;
        _reopenTab?.QueueFree();
        _reopenTab = null;
    }

    /// <summary>Rebuild the panel from current state. Called by
    /// ExpeditionManager.UpdateUI (every move / cast / resource change).</summary>
    public void Refresh()
    {
        if (_manager == null || _list == null)
            return;

        HideDetail(); // rows are about to be rebuilt, so never show stale info

        foreach (var child in _list.GetChildren())
            child.QueueFree();

        // S5.2: header row with the title plus the Hide control.
        var headerRow = new HBoxContainer();
        _list.AddChild(headerRow);

        var header = new Label
        {
            Text = "Grimoire",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        header.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize);
        header.AddThemeColorOverride("font_color", UITheme.Gold);
        headerRow.AddChild(header);

        var hideBtn = new Button { Text = "Hide", Flat = true };
        hideBtn.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize - 4);
        hideBtn.AddThemeColorOverride("font_color", UITheme.TextSecondary);
        hideBtn.Pressed += () => { _panelHidden = true; ApplyHiddenState(); };
        headerRow.AddChild(hideBtn);

        // S5.1: split the castable list. LOADOUT (own-school innates +
        // prepared) stays always visible, the rest grouped by Category under
        // collapsible headers whose state persists (static _sectionOpen).
        var cycle = SaveManager.ActiveSave?.Cycle;
        var grimoire = cycle?.Grimoire;
        string school = cycle?.SelectedSchool ?? "";

        var loadout = new List<OverworldSpellDefinition>();
        var grouped = new System.Collections.Generic.Dictionary<string, List<OverworldSpellDefinition>>();
        foreach (var def in _manager.CastableSpells())
        {
            bool inLoadout = (def.IsInnate && def.School == school) ||
                             (grimoire != null && grimoire.PreparedSpellIds.Contains(def.Id));
            if (inLoadout)
            {
                loadout.Add(def);
            }
            else
            {
                string cat = string.IsNullOrEmpty(def.Category) ? "Other" : def.Category;
                if (!grouped.TryGetValue(cat, out var bucket))
                    grouped[cat] = bucket = new List<OverworldSpellDefinition>();
                bucket.Add(def);
            }
        }

        foreach (var def in loadout)
            _list.AddChild(MakeSpellRow(def, isScroll: false, scrollsHeld: 0));

        // Category sections, §6 order first, any stragglers after.
        var orderedCats = new List<string>();
        foreach (var cat in CategoryOrder)
            if (grouped.ContainsKey(cat))
                orderedCats.Add(cat);
        foreach (var cat in grouped.Keys)
            if (!orderedCats.Contains(cat))
                orderedCats.Add(cat);

        foreach (var cat in orderedCats)
        {
            var bucket = grouped[cat];
            _list.AddChild(MakeSectionHeader(cat, bucket.Count));
            if (!IsOpen(cat))
                continue;
            foreach (var def in bucket)
                _list.AddChild(MakeSpellRow(def, isScroll: false, scrollsHeld: 0));
        }

        // S4 (§8a): the scroll satchel gets its own collapsible section.
        if (grimoire != null && grimoire.ScrollInventory.Count > 0)
        {
            int scrollKinds = 0;
            foreach (var kvp in grimoire.ScrollInventory)
                if (kvp.Value > 0 && OverworldSpellRegistry.Get(kvp.Key) != null)
                    scrollKinds++;
            if (scrollKinds > 0)
            {
                _list.AddChild(MakeSectionHeader("Scrolls", scrollKinds));
                if (IsOpen("Scrolls"))
                {
                    foreach (var kvp in grimoire.ScrollInventory)
                    {
                        if (kvp.Value <= 0)
                            continue;
                        var def = OverworldSpellRegistry.Get(kvp.Key);
                        if (def == null)
                            continue;
                        _list.AddChild(MakeSpellRow(def, isScroll: true, scrollsHeld: kvp.Value));
                    }
                }
            }
        }

        // Active timed effects, if any (Verdant Passage (3) · Campward (armed)).
        string status = OverworldSpellEffects.StatusSummary();
        if (!string.IsNullOrEmpty(status))
        {
            var statusLbl = new Label
            {
                Text = status,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            statusLbl.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize - 4);
            statusLbl.AddThemeColorOverride("font_color", UITheme.EssenceText);
            _list.AddChild(statusLbl);
        }

        // S4.2: measure AFTER the freed children actually leave the tree
        // (QueueFree is end-of-frame; measuring now would count both sets).
        CallDeferred(nameof(ClampListHeight));
    }

    /// <summary>Size the scroller to its content, clamped to a viewport
    /// share (short lists sit tight, long lists scroll), then pin the
    /// panel's rect: bottom edge fixed at PanelBottom, top edge derived
    /// from the panel's own minimum height. No SetSize (it re-anchors the
    /// top-left and pushes a bottom-anchored panel off-screen).</summary>
    private void ClampListHeight()
    {
        if (_scroll == null || _list == null || !IsInsideTree())
            return;
        float want = _list.GetCombinedMinimumSize().Y;
        float cap = GetViewportRect().Size.Y * MaxListViewportShare;
        _scroll.CustomMinimumSize = new Vector2(0, Mathf.Min(want, cap));

        // Panel min height = scroll height + margins + stylebox padding,
        // all already folded into the combined minimum. Pin the rect.
        float panelH = GetCombinedMinimumSize().Y;
        OffsetBottom = PanelBottom;
        OffsetTop = PanelBottom - panelH;
    }

    // ── S5.1: row + section builders ─────────────────────────────────────

    /// <summary>One castable row (spell or scroll): cost breakdown, the
    /// detection tag, magnitude tint, hover→detail card, click→cast.</summary>
    private Button MakeSpellRow(OverworldSpellDefinition def, bool isScroll, int scrollsHeld)
    {
        string block = _manager.CastBlockReason(def, ignoreEssence: isScroll);

        // S3: cost breakdown covering base, off-caster tax, corruption surcharge.
        string costText;
        if (isScroll)
        {
            costText = $"×{scrollsHeld}   ·   0✦";
        }
        else
        {
            int tax = _manager.OffCasterTax(def);
            int surcharge = _manager.CorruptionSurcharge();
            costText = def.EssenceCost.ToString();
            if (tax > 0)
                costText += $"+{tax}";
            if (surcharge > 0)
                costText += $"+{surcharge}";
            costText += "✦";
        }

        // S5.1 (user request): a TEXT detection tag, not just the tint.
        // Only Overt/Grand are marked; an unmarked row is Subtle (quiet).
        string detect = def.Magnitude switch
        {
            "Overt" => "  ·  OVERT",
            "Grand" => "  ·  GRAND",
            _ => "",
        };

        var btn = new Button
        {
            Text = $"{def.Name}   ·   {costText}{detect}",
            Disabled = block != null,
            Alignment = HorizontalAlignment.Left,
        };
        btn.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize - 2);
        UITheme.ApplyButtonStyle(btn, isPrimary: false);
        btn.AddThemeColorOverride("font_color", MagnitudeColor(def.Magnitude));

        string id = def.Id;     // capture per-iteration, not loop variables
        var hoverDef = def;     // (disabled rows still hover, and the card explains WHY)
        bool scroll = isScroll;
        int held = scrollsHeld;
        btn.Pressed += () =>
        {
            if (scroll) _manager.RequestScrollCast(id);
            else _manager.RequestCast(id);
        };
        btn.MouseEntered += () => ShowDetail(hoverDef, scroll, held);
        btn.MouseExited += HideDetail;
        return btn;
    }

    /// <summary>A collapsible section header: "▸ Divination (4)" /
    /// "▾ Divination (4)". Toggles persist in the static _sectionOpen map, so
    /// state survives rebuilds, combat round-trips, and expeditions.</summary>
    private Button MakeSectionHeader(string key, int count)
    {
        bool open = IsOpen(key);
        var btn = new Button
        {
            Text = $"{(open ? "▾" : "▸")}  {key}  ({count})",
            Alignment = HorizontalAlignment.Left,
            Flat = true,
        };
        btn.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize - 3);
        btn.AddThemeColorOverride("font_color", UITheme.Gold);
        btn.Pressed += () =>
        {
            _sectionOpen[key] = !IsOpen(key);
            Refresh();
        };
        return btn;
    }

    private static Color MagnitudeColor(string magnitude) => magnitude switch
    {
        "Overt" => UITheme.MagnitudeOvert,
        "Grand" => UITheme.MagnitudeGrand,
        _ => UITheme.TextPrimary,
    };
}
