using Godot;
using System.Collections.Generic;
using static CampusUi;

// ============================================================
// CampusWorkshopPanel.cs
//
// Purpose:        Q5, the Enchanter's Workshop tab: the sole
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
                   "handcrafted scripts only. At its third tier the Workshop takes a doctrine, " +
                   "chartered at the Grand Hall: the Unbinding Floor cleanses blight, the " +
                   "Attunement Forge rebinds staves.",
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
        if (WorkshopEnchants.IsOverrun(save))
        {
            _container.AddChild(MakeStubLabel(
                "The Workshop stands on overrun ground and has stopped working. Cleanse the land " +
                "(on its campus card) to bring it back."));
            return;
        }

        string doctrine = WorkshopEnchants.CanCleanse(save) ? "full catalog, and the Unbinding Floor (Cleanse)."
                        : WorkshopEnchants.CanAttune(save) ? "full catalog, and the Attunement Forge (staves)."
                        : Charters.IsRefitting(save, WorkshopEnchants.BuildingId) ? "full catalog; a doctrine is being fitted and holds from the next moon."
                        : "full catalog; no doctrine chartered (charter one at the Grand Hall).";
        var tierLabel = new Label
        {
            Text = $"Workshop tier {tier}: " + tier switch
            {
                1 => "stat-line enchants.",
                2 => "stat lines and scripted effects.",
                _ => doctrine,
            },
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
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
            { Text = $"BLIGHTED: {WorkshopEnchants.DrawbackText(item.DrawbackKey)}. Enchant slot sealed." };
            blight.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
            blight.AddThemeColorOverride("font_color", UITheme.Danger);
            blight.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            col.AddChild(blight);

            bool canCleanse = WorkshopEnchants.CanCleanse(save);
            var cleanse = new Button
            {
                Text = canCleanse
                    ? $"Cleanse ({WorkshopEnchants.CleanseGold}g + {WorkshopEnchants.CleanseSplinters} splinters)"
                    : "Cleanse (requires the Unbinding Floor, chartered at the Grand Hall)",
                Disabled = !canCleanse || save.Gold < WorkshopEnchants.CleanseGold
                           || save.ArcaneSplinters < WorkshopEnchants.CleanseSplinters,
                SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,
            };
            cleanse.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
            UITheme.ApplyButtonStyle(cleanse, isPrimary: !cleanse.Disabled);
            string capturedId = item.InstanceId;
            cleanse.Pressed += () =>
            {
                var it = FindItem(capturedId);
                if (it != null && WorkshopEnchants.TryCleanse(it) != null)
                    Refresh();
            };
            col.AddChild(cleanse);
            return card; // sealed slot, so no enchant verbs while blighted
        }

        // Attunement Forge: a staff's Innate card can be rebound (M7 binding).
        if (def != null && def.IsStaff && WorkshopEnchants.CanAttune(save))
            col.AddChild(BuildAttuneRow(item, def, save));

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
                int cost = WorkshopEnchants.EnchantCost(save, e);
                bool tainting = WorkshopEnchants.IsTainting(save);
                var btn = new Button
                {
                    Text = $"{e.Name} ({cost}g)",
                    TooltipText = e.Description + (tainting
                        ? " Blighted Workshop: half price, but the item takes a drawback and its slot seals."
                        : ""),
                    Disabled = save.Gold < cost,
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

    /// <summary>Wand-style picker for the Attunement Forge: choose a deck card,
    /// pay, and the staff opens every fight with it.</summary>
    private readonly Dictionary<string, string> _attuneChoice = new();

    private Control BuildAttuneRow(ItemInstance staff, ItemDefinition def, GuildSaveData save)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);

        var candidates = WorkshopEnchants.AttuneCandidates(save);
        string bound = staff.EffectiveBoundCardId(def);
        var boundBp = CardDatabase.Blueprints.Find(b => b.Id == bound);
        var current = new Label
        {
            Text = $"Opens with: {(boundBp != null ? CardDatabase.GetDisplayName(boundBp) : "nothing")}",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        current.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        row.AddChild(current);
        if (candidates.Count == 0)
            return row;

        string staffId = staff.InstanceId;
        _attuneChoice.TryGetValue(staffId, out string chosen);
        var picker = new OptionButton { CustomMinimumSize = new Vector2(240, 30) };
        picker.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        int selected = 0;
        for (int i = 0; i < candidates.Count; i++)
        {
            picker.AddItem(CardDatabase.GetDisplayName(candidates[i]), i);
            if (candidates[i].Id == chosen)
                selected = i;
        }
        _attuneChoice[staffId] = candidates[selected].Id;
        picker.Select(selected);
        picker.ItemSelected += idx =>
        {
            if (idx >= 0 && idx < candidates.Count)
            {
                _attuneChoice[staffId] = candidates[(int)idx].Id;
                Refresh();
            }
        };
        row.AddChild(picker);

        string why = WorkshopEnchants.CannotAttuneReason(save, staff, _attuneChoice[staffId]);
        var btn = new Button
        {
            Text = $"Attune ({WorkshopEnchants.AttuneGold}g)",
            Disabled = why != null,
            TooltipText = why ?? "",
        };
        btn.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        UITheme.ApplyButtonStyle(btn, isPrimary: false);
        btn.Pressed += () =>
        {
            var it = FindItem(staffId);
            if (it != null && WorkshopEnchants.TryAttune(Ctx?.Save, it, _attuneChoice[staffId]) != null)
                Refresh();
        };
        row.AddChild(btn);
        return row;
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
