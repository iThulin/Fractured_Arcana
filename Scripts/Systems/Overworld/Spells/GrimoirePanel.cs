using Godot;

// ============================================================
// GrimoirePanel.cs  (S2, 2026-07-15)
//
// Purpose:        The Grimoire — the overworld HUD's spell panel,
//                 bottom-left. Lists the expedition's castable
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
//                 S4: a Scrolls section under the spell list —
//                 Essence-free casts from GrimoireState.
//                 ScrollInventory, routed through
//                 RequestScrollCast.
//                 S4.1 (user request 2026-07-16): the native
//                 tooltip is gone — hovering a row opens an
//                 OPAQUE detail card to the panel's right (name,
//                 school · category · magnitude, live cost
//                 breakdown, full description, block reason).
//                 The card is a SIBLING on the HUD canvas (added
//                 deferred — a PanelContainer would layout-manage
//                 a child), freed in _ExitTree.
// Layer:          UI
// Collaborators:  OverworldSpellManager.cs (data + cast entry),
//                 ExpeditionManager.cs (creates + refreshes),
//                 UITheme.cs (colors)
// See:            overworld_spell_system_v1_1.docx §12
// ============================================================

/// <summary>Bottom-left overworld spell panel. Refresh() rebuilds from the
/// manager's castable list — cheap at ≤ a dozen rows.</summary>
public partial class GrimoirePanel : PanelContainer
{
    /// <summary>Layout constants shared by the panel and its detail card.</summary>
    private const float PanelLeft = 12, PanelRight = 292, PanelBottom = -12;
    private const float DetailGap = 8, DetailWidth = 340;

    /// <summary>Cap on the spell list's height, as a fraction of the viewport.
    /// Debug mode exposes all 36 implemented spells — without a clamp the
    /// panel runs off the top of the screen (user report, 2026-07-16).</summary>
    private const float MaxListViewportShare = 0.55f;

    private OverworldSpellManager _manager;
    private ScrollContainer _scroll;
    private VBoxContainer _list;

    // ── S4.1: hover detail card (sibling on the HUD canvas) ─────────────
    private PanelContainer _detail;
    private VBoxContainer _detailBox;

    public void Initialize(OverworldSpellManager manager)
    {
        _manager = manager;

        // Bottom-left anchor. S4.2 fix: the rect is managed EXPLICITLY —
        // bottom edge pinned at PanelBottom, top edge set from content in
        // ClampListHeight. (The earlier grow-direction + SetSize approach
        // re-anchored the TOP-left on resize, which laid the panel out
        // DOWNWARD off the bottom of the screen — user screenshot bug.)
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

        // S4.1: the detail card must be a SIBLING (this PanelContainer would
        // layout-manage it as a child); the parent exists by now, but defer
        // to stay safe against setup-order changes.
        CallDeferred(nameof(CreateDetailCard));
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
            // Read-only card — never steal clicks from the map beneath.
            MouseFilter = MouseFilterEnum.Ignore,
        };

        // FULLY OPAQUE — the whole point. The HUD background color reads
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

        // Cost line — the live breakdown, or the scroll note.
        if (isScroll)
        {
            Add($"Scroll ×{scrollsHeld} — no Essence; spent on a successful cast", -2,
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

        // Full description — wrapped, never truncated, never translucent.
        Add(def.Description, -2, UITheme.TextPrimary, wrap: true);

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
        // The card is a sibling, not a child — free it explicitly.
        _detail?.QueueFree();
        _detail = null;
        _detailBox = null;
    }

    /// <summary>Rebuild the panel from current state. Called by
    /// ExpeditionManager.UpdateUI (every move / cast / resource change).</summary>
    public void Refresh()
    {
        if (_manager == null || _list == null)
            return;

        HideDetail(); // rows are about to be rebuilt — never show stale info

        foreach (var child in _list.GetChildren())
            child.QueueFree();

        var header = new Label { Text = "Grimoire" };
        header.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize);
        header.AddThemeColorOverride("font_color", UITheme.Gold);
        _list.AddChild(header);

        int surcharge = _manager.CorruptionSurcharge();

        foreach (var def in _manager.CastableSpells())
        {
            string block = _manager.CastBlockReason(def);

            // S3: cost breakdown — base, off-caster tax, corruption surcharge.
            int tax = _manager.OffCasterTax(def);
            string costText = def.EssenceCost.ToString();
            if (tax > 0)
                costText += $"+{tax}";
            if (surcharge > 0)
                costText += $"+{surcharge}";

            // S4.1: no native tooltip — the opaque detail card replaces it.
            var btn = new Button
            {
                Text = $"{def.Name}   ·   {costText}✦",
                Disabled = block != null,
                Alignment = HorizontalAlignment.Left,
            };
            btn.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize - 2);
            UITheme.ApplyButtonStyle(btn, isPrimary: false);
            btn.AddThemeColorOverride("font_color", MagnitudeColor(def.Magnitude));

            string id = def.Id;      // capture per-iteration, not the loop variable
            var hoverDef = def;      // (disabled buttons still emit hover — intended:
            btn.Pressed += () => _manager.RequestCast(id);           // the card explains WHY)
            btn.MouseEntered += () => ShowDetail(hoverDef, isScroll: false, scrollsHeld: 0);
            btn.MouseExited += HideDetail;
            _list.AddChild(btn);
        }

        // S4 (§8a): the scroll satchel — Essence-free single casts, any
        // school, consumed on success. Contextual gates and once-per-
        // expedition caps still disable (with the reason), but never the pool.
        var grimoire = SaveManager.ActiveSave?.Cycle?.Grimoire;
        if (grimoire != null && grimoire.ScrollInventory.Count > 0)
        {
            var scrollHeader = new Label { Text = "Scrolls" };
            scrollHeader.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize - 2);
            scrollHeader.AddThemeColorOverride("font_color", UITheme.Gold);
            _list.AddChild(scrollHeader);

            foreach (var kvp in grimoire.ScrollInventory)
            {
                if (kvp.Value <= 0)
                    continue;
                var def = OverworldSpellRegistry.Get(kvp.Key);
                if (def == null)
                    continue;

                string block = _manager.CastBlockReason(def, ignoreEssence: true);
                var sBtn = new Button
                {
                    Text = $"{def.Name} ×{kvp.Value}   ·   0✦",
                    Disabled = block != null,
                    Alignment = HorizontalAlignment.Left,
                };
                sBtn.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize - 2);
                UITheme.ApplyButtonStyle(sBtn, isPrimary: false);
                sBtn.AddThemeColorOverride("font_color", MagnitudeColor(def.Magnitude));

                string sid = def.Id; // capture per-iteration
                var hoverDef = def;
                int held = kvp.Value;
                sBtn.Pressed += () => _manager.RequestScrollCast(sid);
                sBtn.MouseEntered += () => ShowDetail(hoverDef, isScroll: true, scrollsHeld: held);
                sBtn.MouseExited += HideDetail;
                _list.AddChild(sBtn);
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
    /// share — short lists sit tight, long lists scroll — then pin the
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

    private static Color MagnitudeColor(string magnitude) => magnitude switch
    {
        "Overt" => UITheme.MagnitudeOvert,
        "Grand" => UITheme.MagnitudeGrand,
        _ => UITheme.TextPrimary,
    };
}
