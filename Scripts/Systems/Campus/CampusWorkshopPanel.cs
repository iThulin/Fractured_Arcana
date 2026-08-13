using Godot;
using System.Collections.Generic;
using static CampusUi;

// ============================================================
// CampusWorkshopPanel.cs
//
// Purpose:        Q5 — the Enchanter's Workshop tab: the sole
//                 item-mutation venue. Lists the Armory's items
//                 with their innate line, blight state, and the
//                 one enchant slot; verbs are Enchant (tier-gated
//                 catalog) and Cleanse (blighted items, tier 3).
// Layer:          UI (campus)
// Collaborators:  WorkshopEnchants (catalog + verbs),
//                 ItemInstance (slot + blight fields),
//                 CouncilQueries.BuildingTier (the tier gate),
//                 CampusPanel / CampusScreen (hosting).
// ============================================================

/// <summary>The Workshop tab. Follows the standard panel contract: build
/// empty containers, fill in Refresh, tolerate a null save.</summary>
public class CampusWorkshopPanel : CampusPanel
{
    private VBoxContainer _container;

    protected override void OnBuild(ScrollContainer scroll)
    {
        var margins = MakeMargins(32, 20);
        scroll.AddChild(margins);
        var layout = MakeVBox(10);
        margins.AddChild(layout);

        AddSectionHeader(layout, "Enchanter's Workshop");

        var note = new Label
        {
            Text = "The guild's sole venue for item mutation: one enchant slot per item, " +
                   "handcrafted scripts only. Blighted spoils can be cleansed here once " +
                   "the Unbinding Floor stands.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        note.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        note.Modulate = UITheme.CampusSubtleText;
        layout.AddChild(note);
        layout.AddChild(new HSeparator());

        _container = MakeVBox(8);
        layout.AddChild(_container);
    }

    public override void Refresh()
    {
        if (_container == null)
            return;
        foreach (var child in _container.GetChildren())
            child.QueueFree();

        var save = Ctx?.Save;
        if (save == null)
        {
            _container.AddChild(MakeStubLabel("Select a save slot to open the Workshop."));
            return;
        }

        int tier = CouncilQueries.BuildingTier(save, "enchanters_workshop");
        if (tier < 1)
        {
            _container.AddChild(MakeStubLabel(
                "The Enchanter's Workshop is not yet built. Raise it on the campus to begin."));
            return;
        }

        var tierLabel = new Label
        {
            Text = $"Workshop tier {tier} — " + tier switch
            {
                1 => "stat-line enchants.",
                2 => "stat lines and scripted effects.",
                _ => "full catalog, and the Unbinding Floor (Cleanse).",
            },
        };
        tierLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        _container.AddChild(tierLabel);

        if (save.Armory.OwnedItems.Count == 0)
        {
            _container.AddChild(MakeStubLabel("The Armory holds nothing to work on."));
            return;
        }

        foreach (var item in save.Armory.OwnedItems)
            _container.AddChild(BuildItemCard(item, save, tier));
    }

    // ── One item's card ──────────────────────────────────────────────────

    private Control BuildItemCard(ItemInstance item, GuildSaveData save, int tier)
    {
        var card = new PanelContainer();
        card.AddThemeStyleboxOverride("panel", UITheme.MakePanelStyle(
            UITheme.BgCard, UITheme.RarityColor(item.Rarity)));

        var pad = new MarginContainer();
        pad.AddThemeConstantOverride("margin_left", 12);
        pad.AddThemeConstantOverride("margin_right", 12);
        pad.AddThemeConstantOverride("margin_top", 8);
        pad.AddThemeConstantOverride("margin_bottom", 8);
        card.AddChild(pad);

        var col = MakeVBox(4);
        pad.AddChild(col);

        var name = new Label { Text = $"{item.Name}  ·  {item.Rarity} {item.Slot}" };
        name.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
        name.AddThemeColorOverride("font_color", UITheme.RarityColor(item.Rarity));
        col.AddChild(name);

        var def = ItemDatabase.Get(item.DefinitionId);
        if (def != null && !string.IsNullOrEmpty(def.Description))
        {
            var innate = new Label { Text = def.Description +
                (item.BlightBonus > 0 ? "  (blight-strengthened)" : "") };
            innate.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
            innate.AddThemeColorOverride("font_color", UITheme.TextDim);
            innate.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            col.AddChild(innate);
        }

        // Blight line + Cleanse verb
        if (item.IsBlighted)
        {
            var blight = new Label
            { Text = $"BLIGHTED — {WorkshopEnchants.DrawbackText(item.DrawbackKey)}. Enchant slot sealed." };
            blight.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
            blight.AddThemeColorOverride("font_color", UITheme.Danger);
            blight.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            col.AddChild(blight);

            var cleanse = new Button
            {
                Text = tier >= 3
                    ? $"Cleanse ({WorkshopEnchants.CleanseGold}g + {WorkshopEnchants.CleanseSplinters} splinters)"
                    : "Cleanse (requires the Unbinding Floor — tier 3)",
                Disabled = tier < 3 || save.Gold < WorkshopEnchants.CleanseGold
                           || save.ArcaneSplinters < WorkshopEnchants.CleanseSplinters,
                SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,
            };
            cleanse.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
            UITheme.ApplyButtonStyle(cleanse, isPrimary: !cleanse.Disabled);
            string capturedId = item.InstanceId;
            cleanse.Pressed += () =>
            {
                var it = FindItem(capturedId);
                if (it != null && WorkshopEnchants.TryCleanse(it,
                        CouncilQueries.BuildingTier(Ctx?.Save, "enchanters_workshop")) != null)
                    Refresh();
            };
            col.AddChild(cleanse);
            return card; // sealed slot — no enchant verbs while blighted
        }

        // Enchant slot state
        var slotLabel = new Label
        {
            Text = string.IsNullOrEmpty(item.EnchantKey)
                ? "Enchant slot: empty."
                : $"Enchant slot: {EnchantDisplayName(item)} (re-enchanting overwrites).",
        };
        slotLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        col.AddChild(slotLabel);

        // Enchant verbs
        var verbs = WorkshopEnchants.AvailableFor(item, tier);
        if (verbs.Count > 0)
        {
            var row = new HFlowContainer();
            row.AddThemeConstantOverride("h_separation", 6);
            row.AddThemeConstantOverride("v_separation", 6);
            col.AddChild(row);

            foreach (var e in verbs)
            {
                var btn = new Button
                {
                    Text = $"{e.Name} ({e.GoldCost}g)",
                    TooltipText = e.Description,
                    Disabled = save.Gold < e.GoldCost,
                };
                btn.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
                UITheme.ApplyButtonStyle(btn, isPrimary: false);
                string capturedItem = item.InstanceId;
                string capturedEnchant = e.Id;
                btn.Pressed += () =>
                {
                    var it = FindItem(capturedItem);
                    if (it != null && WorkshopEnchants.TryEnchant(it, capturedEnchant,
                            CouncilQueries.BuildingTier(Ctx?.Save, "enchanters_workshop")) != null)
                        Refresh();
                };
                row.AddChild(btn);
            }
        }

        return card;
    }

    private ItemInstance FindItem(string instanceId)
    {
        var save = Ctx?.Save;
        if (save == null) return null;
        foreach (var i in save.Armory.OwnedItems)
            if (i.InstanceId == instanceId) return i;
        return null;
    }

    private static string EnchantDisplayName(ItemInstance item)
    {
        foreach (var e in WorkshopEnchants.Catalog)
            if (e.Key == item.EnchantKey && e.Value == item.EnchantValue
                && e.Param == item.EnchantParam)
                return e.Name;
        return item.EnchantKey;
    }
}
