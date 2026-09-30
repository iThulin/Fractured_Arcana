using Godot;
using System.Collections.Generic;
using static CampusUi;

// ============================================================
// CharterSection.cs
//
// Purpose:        The Grand Hall's charter list (campus_building_
//                 upgrades_design_v1 §2b): how many charters the hall
//                 holds, which doctrines are chartered, and a Charter /
//                 Unseat verb on every doctrine the campus has built.
//                 A section, not a panel: it fills a container the
//                 Guild panel owns, so the Grand Hall stays one room.
// Layer:          UI (campus)
// Collaborators:  Charters.cs (every rule), CampusGuildPanel.cs (host),
//                 BuildingDatabase (templates), CampusContext
//                 (refresh and toasts).
// ============================================================

/// <summary>Draws the charter list into a container. Stateless.</summary>
public static class CharterSection
{
    /// <summary>Clear <paramref name="container"/> and draw the charters.
    /// <paramref name="onChanged"/> runs after any charter changes (the host
    /// refreshes itself and anything showing gold or materials).</summary>
    public static void Fill(VBoxContainer container, CampusContext ctx, System.Action onChanged)
    {
        if (container == null)
        {
            return;
        }
        foreach (var child in container.GetChildren())
        {
            child.QueueFree();
        }

        var save = ctx?.Save;
        if (save?.Cycle == null)
        {
            container.AddChild(MakeStubLabel("Select a save slot to see the charters."));
            return;
        }

        int slots = Charters.Slots(save);
        int used = Charters.Used(save);
        string cost = Charters.NextFillIsFree(save)
            ? "The next charter this timeline is free and holds at once."
            : $"Every free charter this timeline is spent: a new or changed charter is a refit ({Charters.RefitMaterials} materials, holds from the next moon).";
        AddLine(container,
            $"Charters held: {used} / {slots}. A building's third tier offers two doctrines; only a chartered one is in force, and an unchartered building works at its second tier. {cost}",
            UITheme.TextPrimary);

        var buildings = new List<(Building template, BuildingTier tier)>();
        foreach (var template in BuildingDatabase.LoadAll())
        {
            var tier = Charters.DoctrineTier(template);
            if (tier != null && Charters.CanHoldDoctrine(save, template.Id))
            {
                buildings.Add((template, tier));
            }
        }
        if (buildings.Count == 0)
        {
            container.AddChild(MakeStubLabel(
                "No building has reached its doctrine tier yet. Raise one to its third tier to charter a doctrine."));
            return;
        }
        buildings.Sort((a, b) => string.CompareOrdinal(a.template.Name, b.template.Name));

        foreach (var (template, tier) in buildings)
        {
            container.AddChild(BuildBuildingCard(template, tier, save, ctx, onChanged));
        }
    }

    private static Control BuildBuildingCard(Building template, BuildingTier tier, GuildSaveData save,
                                             CampusContext ctx, System.Action onChanged)
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
        var title = new Label { Text = $"{template.Name}  ·  {state}" };
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
                    string line = Charters.Unseat(ctx?.Save, buildingId);
                    if (line != null)
                    {
                        ctx.Toasts?.Push(line, QuestToastKind.Progress);
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
                string line = Charters.TryCharter(ctx?.Save, buildingId, doctrineId);
                if (line != null)
                {
                    ctx.Toasts?.Push(line, QuestToastKind.Progress);
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
