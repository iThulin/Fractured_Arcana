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
    private OverworldSpellManager _manager;
    private VBoxContainer _list;

    public void Initialize(OverworldSpellManager manager)
    {
        _manager = manager;

        // Bottom-left anchor; grows upward.
        AnchorLeft = 0f; AnchorRight = 0f;
        AnchorTop = 1f; AnchorBottom = 1f;
        GrowVertical = Control.GrowDirection.Begin;
        OffsetLeft = 12;
        OffsetRight = 292;
        OffsetBottom = -12;

        AddThemeStyleboxOverride("panel",
            UITheme.MakePanelStyle(UITheme.OverworldHudBg, UITheme.OverworldHudBorder));

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 10);
        margin.AddThemeConstantOverride("margin_right", 10);
        margin.AddThemeConstantOverride("margin_top", 8);
        margin.AddThemeConstantOverride("margin_bottom", 8);
        AddChild(margin);

        _list = new VBoxContainer();
        _list.AddThemeConstantOverride("separation", 4);
        margin.AddChild(_list);

        Refresh();
    }

    /// <summary>Rebuild the panel from current state. Called by
    /// ExpeditionManager.UpdateUI (every move / cast / resource change).</summary>
    public void Refresh()
    {
        if (_manager == null || _list == null)
            return;

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

            var btn = new Button
            {
                Text = $"{def.Name}   ·   {costText}✦",
                Disabled = block != null,
                TooltipText = def.Description +
                              $"\n{def.Category} · {def.Magnitude} · {costText} Essence" +
                              (tax > 0 ? " (+1 off-school)" : "") +
                              (surcharge > 0 ? $" (+{surcharge} corrupted ground)" : "") +
                              (block != null ? $"\n[{block}]" : ""),
                Alignment = HorizontalAlignment.Left,
            };
            btn.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize - 2);
            UITheme.ApplyButtonStyle(btn, isPrimary: false);
            btn.AddThemeColorOverride("font_color", MagnitudeColor(def.Magnitude));

            string id = def.Id; // capture per-iteration, not the loop variable
            btn.Pressed += () => _manager.RequestCast(id);
            _list.AddChild(btn);
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
    }

    private static Color MagnitudeColor(string magnitude) => magnitude switch
    {
        "Overt" => UITheme.MagnitudeOvert,
        "Grand" => UITheme.MagnitudeGrand,
        _ => UITheme.TextPrimary,
    };
}
