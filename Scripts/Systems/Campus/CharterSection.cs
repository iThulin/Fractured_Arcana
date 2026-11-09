using Godot;
using System.Collections.Generic;
using static CampusUi;

// ============================================================
// CharterSection.cs
//
// Purpose:        The charters (campus_building_upgrades_design_v1
//                 §2b), drawn where they are decided since 2026-11-05
//                 (docs/guild_screen_retirement_v1.md, B2): each
//                 building's card shows its two doctrines with Charter
//                 and Unseat (FillFor), and the Grand Hall's card shows
//                 the seats held and which doctrine each building holds
//                 (FillSummary). The whole list sat in the Guild panel
//                 until then.
// Layer:          UI (campus)
// Collaborators:  Charters.cs (every rule), HomeBuildingPanelHost.cs
//                 (the building card that hosts both), BuildingDatabase
//                 (templates), ToastManager (the card's toasts).
// ============================================================

/// <summary>Draws the charters into a building card's container. Stateless.</summary>
public static class CharterSection
{
    /// <summary>The Grand Hall's card: the seats held against the seats the hall gives, what
    /// the next fill costs, and the doctrine each chartered building holds. The verbs are in
    /// each building's own card (<see cref="FillFor"/>).</summary>
    public static void FillSummary(VBoxContainer container, GuildSaveData save)
    {
        if (container == null)
        {
            return;
        }
        Clear(container);
        if (save?.Cycle == null)
        {
            return;
        }

        int slots = Charters.Slots(save);
        int used = Charters.Used(save);
        string cost = Charters.NextFillIsFree(save)
            ? "The next charter this timeline is free and holds at once."
            : $"Every free charter this timeline is spent: a new or changed charter is a refit ({Charters.RefitMaterials} materials, holds from the next moon).";
        AddLine(container, $"Charters held: {used} / {slots}. {cost}", UITheme.TextPrimary);

        var held = new List<string>();
        foreach (var entry in save.Cycle.Charters ?? new List<CharterEntry>())
        {
            if (entry == null)
            {
                continue;
            }
            var template = BuildingDatabase.GetTemplate(entry.BuildingId);
            var doctrine = Charters.Doctrine(entry.BuildingId, entry.DoctrineId);
            if (template != null && doctrine != null)
            {
                held.Add($"{template.Name}: {doctrine.Name}");
            }
        }
        held.Sort(string.CompareOrdinal);
        AddLine(container,
            held.Count == 0
                ? "No building holds a charter. A building's third tier offers two doctrines: charter one from that building's card."
                : "Chartered: " + string.Join("; ", held) + ".",
            held.Count == 0 ? UITheme.TextDim : UITheme.Gold);
    }

    /// <summary>One building's doctrines, for that building's own card. At its doctrine tier:
    /// the two doctrines with Charter and Unseat. One tier short: a line naming what that tier
    /// offers, to weigh before buying it. Otherwise, and for a building without doctrines,
    /// nothing. <paramref name="onChanged"/> runs after a charter changes.</summary>
    public static void FillFor(VBoxContainer container, string buildingId, GuildSaveData save,
                               ToastManager toasts, System.Action onChanged)
    {
        if (container == null)
        {
            return;
        }
        Clear(container);
        var template = BuildingDatabase.GetTemplate(buildingId);
        var tier = Charters.DoctrineTier(template);
        if (save?.Cycle == null || tier == null)
        {
            return;
        }
        if (Charters.CanHoldDoctrine(save, buildingId))
        {
            RegisterManager.Fire("campus.first_charter");   // S5: taught when first met
            container.AddChild(BuildBuildingCard(template, tier, save, toasts, onChanged));
            return;
        }
        if (TierOf(save, buildingId) + 1 < tier.Tier)
        {
            return;
        }
        var names = new List<string>();
        foreach (var d in tier.Doctrines)
        {
            names.Add(d.IsBlocked ? $"{d.Name} (not yet)" : d.Name);
        }
        AddLine(container,
            $"At tier {tier.Tier}, a doctrine: {string.Join(" or ", names)}. Only a chartered one is in force, and the Grand Hall's tiers set how many the guild holds.",
            UITheme.TextDim);
    }

    private static int TierOf(GuildSaveData save, string buildingId)
    {
        if (save?.Buildings == null)
        {
            return 0;
        }
        foreach (var b in save.Buildings)
        {
            if (b != null && b.Id == buildingId)
            {
                return b.Tier;
            }
        }
        return 0;
    }

    private static void Clear(VBoxContainer container)
    {
        foreach (var child in container.GetChildren())
        {
            child.QueueFree();
        }
    }

    private static Control BuildBuildingCard(Building template, BuildingTier tier, GuildSaveData save,
                                             ToastManager toasts, System.Action onChanged)
    {
        var card = new PanelContainer();
        card.AddThemeStyleboxOverride("panel", UITheme.MakePanelStyle(UITheme.BgCard, UITheme.Gold));
        var pad = new MarginContainer();
        pad.AddThemeConstantOverride("margin_left", 12);
        pad.AddThemeConstantOverride("margin_right", 12);
        pad.AddThemeConstantOverride("margin_top", 8);
        pad.AddThemeConstantOverride("margin_bottom", 8);
        card.AddChild(pad);
        var col = MakeVBox(4);
        pad.AddChild(col);

        var entry = Charters.EntryFor(save, template.Id);
        string state = entry == null ? "no charter"
                     : CampusBlight.IsBlighted(save, template.Id) ? "chartered, but dark on blighted ground"
                     : Charters.IsOverSlots(save, template.Id) ? "chartered, but dark: the Grand Hall is short a seat"
                     : Charters.IsRefitting(save, template.Id) ? "refitting, holds from the next moon"
                     : "chartered";
        if (Charters.IsFreeSeat(save, template.Id))
            state += "  ·  your school's seat: chartered free, no slot used";
        var title = new Label { Text = $"Doctrine: {state}" };   // the card's header names the building
        title.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
        title.AddThemeColorOverride("font_color", UITheme.TextPrimary);
        col.AddChild(title);

        foreach (var d in tier.Doctrines)
        {
            bool isThis = entry != null && entry.DoctrineId == d.Id;
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 10);
            col.AddChild(row);

            var text = new Label
            {
                Text = $"{d.Name}: {d.Description}" + (d.IsBlocked ? $"  (Not yet: {d.Blocked})" : ""),
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            };
            text.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
            text.AddThemeColorOverride("font_color", isThis ? UITheme.Gold : d.IsBlocked ? UITheme.TextDim : UITheme.TextPrimary);
            row.AddChild(text);

            string buildingId = template.Id;
            string doctrineId = d.Id;
            if (isThis)
            {
                var unseat = MakeButton("Unseat", 120, 32, UITheme.CampusSmallFontSize, isPrimary: false);
                unseat.TooltipText = "Frees this charter. The fill is not refunded.";
                unseat.Pressed += () =>
                {
                    string line = Charters.Unseat(save, buildingId);
                    if (line != null)
                    {
                        toasts?.Push(line, QuestToastKind.Progress);
                        onChanged?.Invoke();
                    }
                };
                row.AddChild(unseat);
                continue;
            }

            string why = Charters.CannotCharterReason(save, buildingId, doctrineId);
            bool refit = Charters.IsRefit(save, buildingId);
            var charter = MakeButton(refit ? $"Charter ({Charters.RefitMaterials} m)" : "Charter", 140, 32,
                                     UITheme.CampusSmallFontSize, isPrimary: why == null);
            charter.Disabled = why != null;
            charter.TooltipText = why ?? (refit ? "A refit: paid in materials, in force from the next moon." : "Free, and in force at once.");
            charter.Pressed += () =>
            {
                string line = Charters.TryCharter(save, buildingId, doctrineId);
                if (line != null)
                {
                    toasts?.Push(line, QuestToastKind.Progress);
                    onChanged?.Invoke();
                }
            };
            row.AddChild(charter);
        }
        return card;
    }

    private static void AddLine(VBoxContainer parent, string text, Color color)
    {
        var lbl = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        lbl.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        lbl.AddThemeColorOverride("font_color", color);
        parent.AddChild(lbl);
    }
}
