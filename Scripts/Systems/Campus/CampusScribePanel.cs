using Godot;
using System.Collections.Generic;
using static CampusUi;

// ============================================================
// CampusScribePanel.cs
//
// Purpose:        The Scribe's Tower's room: overworld scrolls,
//                 spellglass sealing and (Wandwright) wand rebinding.
//                 Replaces the interim Scriptorium sections that lived
//                 on the Armory and Expedition tabs (both pointed here
//                 as the eventual home, R8/D7).
// Layer:          UI (campus)
// Collaborators:  ScribesTower.cs (every gate and transaction),
//                 Charters.cs (doctrine state for the header line),
//                 CampusPanel / CampusScreen (hosting),
//                 CampusLocationRegistry ("scribe" door).
// See:            docs/campus_building_upgrades_design_v1.md §4
// ============================================================

/// <summary>The Scribe's Tower tab. Standard panel contract: build empty
/// containers, fill in Refresh, tolerate a null save.</summary>
public sealed class CampusScribePanel : CampusPanel
{
    private VBoxContainer _container;

    /// <summary>Wand instance id → the card chosen in its rebind picker. Kept
    /// across refreshes so a rebuild does not reset a half-made choice.</summary>
    private readonly Dictionary<string, string> _rebindChoice = new();

    protected override void OnBuild(ScrollContainer scroll)
    {
        var margins = MakeMargins(32, 20);
        scroll.AddChild(margins);
        var layout = MakeVBox(10);
        margins.AddChild(layout);

        AddSectionHeader(layout, "The Scribe's Tower");
        var note = new Label
        {
            Text = "Scrolls for the road, spells sealed in glass for the fight, and the " +
                   "binding of wands. Glass and scrolls wait in the Armory once made.",
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
        {
            return;
        }
        foreach (var child in _container.GetChildren())
        {
            child.QueueFree();
        }

        var save = Ctx?.Save;
        if (save == null)
        {
            _container.AddChild(MakeStubLabel("Select a save slot to open the Tower."));
            return;
        }

        int tier = ScribesTower.Tier(save);
        if (tier < 1 && CampusBlight.Level(save, ScribesTower.Id) >= CampusBlight.MaxLevel)
        {
            _container.AddChild(MakeStubLabel(
                "The Scribe's Tower stands on overrun ground and has stopped working. Cleanse the land " +
                "(on its campus card) to bring it back."));
            return;
        }
        if (tier < 1)
        {
            _container.AddChild(MakeStubLabel(
                "The Scribe's Tower is not yet built. Raise it on the campus to scribe scrolls and seal spellglass."));
            return;
        }

        AddLine(_container, TierLine(save, tier), UITheme.TextPrimary);

        BuildScrolls(save);
        _container.AddChild(new HSeparator());
        BuildSpellglass(save);
        _container.AddChild(new HSeparator());
        BuildWands(save, tier);
    }

    private static string TierLine(GuildSaveData save, int tier)
    {
        string doctrine = ScribesTower.GlasswrightActive(save) ? " Glasswright chartered."
                        : ScribesTower.WandwrightActive(save) ? " Wandwright chartered."
                        : Charters.IsRefitting(save, ScribesTower.Id) ? " A doctrine is being fitted; it holds from the next moon."
                        : tier >= 3 ? " No doctrine chartered: charter one at the Grand Hall."
                        : "";
        return tier switch
        {
            1 => "Tower tier 1: scrolls, and Common spellglass.",
            2 => "Tower tier 2: Common and Uncommon spellglass; field parties' wands recharge at staging anchors and the castle.",
            _ => "Tower tier 3: the tier 2 craft, and a doctrine." + doctrine,
        };
    }

    // ── Scrolls ──────────────────────────────────────────────────────────

    private void BuildScrolls(GuildSaveData save)
    {
        AddSubheader(_container, "Scrolls");
        AddLine(_container,
            "A scroll holds one cast of a spell the guild knows, usable by any school, " +
            "consuming no Essence, spent on use. Overt magic on a scroll is still witnessed.",
            UITheme.TextDim);

        var scribable = ScribesTower.Scribable(save);
        if (scribable.Count == 0)
        {
            _container.AddChild(MakeStubLabel(
                "The guild knows nothing worth scribing yet. Spells are learned afield " +
                "(lore sites, cordial deals, the dead)."));
            return;
        }

        var grim = save.Cycle.Grimoire;
        foreach (var def in scribable)
        {
            int cost = SpellAcquisition.ScrollGoldCost(def);
            grim.ScrollInventory.TryGetValue(def.Id, out int held);

            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 10);
            _container.AddChild(row);

            var name = new Label
            {
                Text = $"{def.Name}  ·  {def.Magnitude}" + (held > 0 ? $"  ·  ×{held} held" : ""),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                TooltipText = def.Description,
            };
            name.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
            name.AddThemeColorOverride("font_color", UITheme.TextPrimary);
            row.AddChild(name);

            var btn = MakeButton($"Scribe ({cost} g)", 150, 34, UITheme.CampusSmallFontSize, isPrimary: false);
            btn.Disabled = save.Gold < cost;
            var captured = def;
            btn.Pressed += () =>
            {
                string line = ScribesTower.TryScribe(Ctx?.Save, captured);
                if (line != null)
                {
                    Ctx.RefreshGold?.Invoke();
                    Refresh();
                }
            };
            row.AddChild(btn);
        }
    }

    // ── Spellglass ───────────────────────────────────────────────────────

    private void BuildSpellglass(GuildSaveData save)
    {
        AddSubheader(_container, "Spellglass");
        AddLine(_container,
            "A combat spell the guild has unlocked, sealed in glass. Any unit shatters it for " +
            "one cast, mana or no mana, for 1 AP. Carried on a belt." +
            (ScribesTower.GlasswrightActive(save)
                ? " Glasswright: the glass keeps the card's upgrades as your best copy has them."
                : ""),
            UITheme.TextDim);
        if (ScribesTower.IsCracking(save))
        {
            AddLine(_container,
                "The Tower stands on blighted ground. Its glass comes out cracked: any rarity below " +
                $"Legendary, at half the gold, but the shards cut the caster for {ScribesTower.CrackedGlassBite} " +
                "when it breaks. Cleanse the land to seal whole glass again.",
                UITheme.CampusBlightText);
        }

        var candidates = ScribesTower.SealCandidates(save);
        if (candidates.Count == 0)
        {
            _container.AddChild(MakeStubLabel("Nothing the guild knows can be sealed yet."));
            return;
        }

        foreach (var bp in candidates)
        {
            // Rares only appear once the Glasswright could seal them; listing
            // every Rare as a greyed row would bury the ones you can make.
            if (bp.Rarity == CardRarity.Rare && !ScribesTower.GlasswrightActive(save)
                && !ScribesTower.IsCracking(save))
            {
                continue;
            }

            var half = ScribesTower.SealPreview(save, bp) ?? bp.Prebuilt.TopHalf;
            int cost = ScribesTower.SealCost(save, bp.Rarity);
            int held = CountHeldGlass(save, bp.Id);
            string why = ScribesTower.CannotSealReason(save, bp);

            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 10);
            _container.AddChild(row);

            var name = new Label
            {
                Text = $"{half.Name}  ·  {bp.School}  ·  {bp.Rarity}" + (held > 0 ? $"  ·  ×{held} held" : ""),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                TooltipText = half.RulesText,
            };
            name.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
            name.AddThemeColorOverride("font_color", why == null ? UITheme.TextPrimary : UITheme.TextDim);
            row.AddChild(name);

            var btn = MakeButton($"Seal ({cost} g)", 150, 34, UITheme.CampusSmallFontSize, isPrimary: false);
            btn.Disabled = why != null;
            btn.TooltipText = why ?? "";
            string cardId = bp.Id;
            btn.Pressed += () =>
            {
                string line = ScribesTower.TrySeal(Ctx?.Save, cardId);
                if (line != null)
                {
                    Ctx.Toasts?.Push(line, QuestToastKind.Progress);
                    Ctx.RefreshGold?.Invoke();
                    Refresh();
                }
            };
            row.AddChild(btn);
        }
    }

    private static int CountHeldGlass(GuildSaveData save, string blueprintId)
    {
        int n = 0;
        foreach (var inst in save.Armory.OwnedItems)
        {
            if (inst.DefinitionId == "spellglass" && inst.BoundCardId == blueprintId)
            {
                n++;
            }
        }
        return n;
    }

    // ── Wands ────────────────────────────────────────────────────────────

    private void BuildWands(GuildSaveData save, int tier)
    {
        AddSubheader(_container, "Wands");
        AddLine(_container,
            "Wands recharge at home when an expedition ends and with each new moon. " +
            (tier >= 2
                ? "The Tower's second tier lets a field party recharge its wands at any staging anchor or the parked castle."
                : "At the Tower's second tier, a field party also recharges its wands at staging anchors and the castle."),
            UITheme.TextDim);

        if (!ScribesTower.WandwrightActive(save))
        {
            AddLine(_container, tier >= 3
                    ? "Rebinding a wand to another spell needs the Wandwright chartered at the Grand Hall."
                    : "Rebinding wands is a tier 3 doctrine (the Wandwright).",
                UITheme.TextDim);
            return;
        }

        var wands = new List<(ItemInstance inst, ItemDefinition def)>();
        foreach (var inst in save.Armory.OwnedItems)
        {
            var def = ItemDatabase.Get(inst.DefinitionId);
            if (def != null && def.IsWand)
            {
                wands.Add((inst, def));
            }
        }
        if (wands.Count == 0)
        {
            _container.AddChild(MakeStubLabel("The Armory holds no wands. Markets and loot carry them."));
            return;
        }

        var candidates = ScribesTower.RebindCandidates(save);
        if (candidates.Count == 0)
        {
            _container.AddChild(MakeStubLabel("The guild knows no spell a wand can hold."));
            return;
        }

        foreach (var (inst, def) in wands)
        {
            string current = inst.EffectiveBoundCardId(def);
            var currentBp = CardDatabase.Blueprints.Find(b => b.Id == current);
            string currentName = currentBp?.Prebuilt?.TopHalf?.Name ?? current;

            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 10);
            _container.AddChild(row);

            var name = new Label
            {
                Text = $"{inst.Name}  ·  casts {currentName}  ·  charge {System.Math.Max(0, inst.Charges)}/{def.MaxCharges}",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            };
            name.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
            name.AddThemeColorOverride("font_color", UITheme.TextPrimary);
            row.AddChild(name);

            var picker = new OptionButton { CustomMinimumSize = new Vector2(220, 34) };
            picker.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
            string wandId = inst.InstanceId;
            _rebindChoice.TryGetValue(wandId, out string chosen);
            int selected = -1;
            for (int i = 0; i < candidates.Count; i++)
            {
                var bp = candidates[i];
                picker.AddItem($"{bp.Prebuilt.TopHalf.Name} ({bp.School})", i);
                if (bp.Id == chosen)
                {
                    selected = i;
                }
            }
            if (selected < 0)
            {
                selected = 0;
                _rebindChoice[wandId] = candidates[0].Id;
            }
            picker.Select(selected);
            picker.ItemSelected += idx =>
            {
                if (idx >= 0 && idx < candidates.Count)
                {
                    _rebindChoice[wandId] = candidates[(int)idx].Id;
                    Refresh();
                }
            };
            row.AddChild(picker);

            string pick = _rebindChoice[wandId];
            string why = ScribesTower.CannotRebindReason(save, inst, pick);
            var btn = MakeButton($"Rebind ({ScribesTower.RebindGold} g)", 150, 34, UITheme.CampusSmallFontSize, isPrimary: false);
            btn.Disabled = why != null;
            btn.TooltipText = why ?? "";
            btn.Pressed += () =>
            {
                var s = Ctx?.Save;
                var wand = s?.Armory?.GetInstance(wandId);
                string line = ScribesTower.TryRebind(s, wand, _rebindChoice[wandId]);
                if (line != null)
                {
                    Ctx.Toasts?.Push(line, QuestToastKind.Progress);
                    Ctx.RefreshGold?.Invoke();
                    Refresh();
                }
            };
            row.AddChild(btn);
        }
    }

    // ── Small helpers ────────────────────────────────────────────────────

    private static void AddSubheader(VBoxContainer parent, string text)
    {
        var lbl = new Label { Text = text };
        lbl.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
        lbl.AddThemeColorOverride("font_color", UITheme.Gold);
        parent.AddChild(lbl);
    }

    private static void AddLine(VBoxContainer parent, string text, Color color)
    {
        var lbl = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        lbl.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        lbl.AddThemeColorOverride("font_color", color);
        parent.AddChild(lbl);
    }
}
