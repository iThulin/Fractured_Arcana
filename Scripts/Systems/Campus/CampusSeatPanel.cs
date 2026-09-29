using Godot;
using System.Collections.Generic;
using static CampusUi;

// ============================================================
// CampusSeatPanel.cs
//
// Purpose:        The School Seats room (campus_building_upgrades_
//                 design_v1 §8): one card per school seat the campus
//                 knows, with its tier, the facets held and where the
//                 missing ones come from, the next tier's gate, the
//                 doctrine in force, and the seat's own controls. The
//                 Crucible of Storms' control is the front picker.
// Layer:          UI (campus)
// Collaborators:  Facets.cs (counts, sources, hints), SchoolSeats.cs
//                 (front), Charters.cs (doctrine state),
//                 CampusConstruction (next-tier gate text),
//                 CampusScreen / HomeBuildingPanelHost (hosting),
//                 CampusLocationRegistry ("seats" door).
// ============================================================

/// <summary>The School Seats tab. Standard panel contract.</summary>
public sealed class CampusSeatPanel : CampusPanel
{
    private VBoxContainer _container;

    protected override void OnBuild(ScrollContainer scroll)
    {
        var margins = MakeMargins(32, 20);
        scroll.AddChild(margins);
        var layout = MakeVBox(10);
        margins.AddChild(layout);

        AddSectionHeader(layout, "School Seats");
        var note = new Label
        {
            Text = "One seat per school. A seat is raised once its discipline is declared; its higher " +
                   "tiers wait on facets, the faces of that school's fragment, recovered in the field. " +
                   "The seat of the school you play this timeline is chartered free.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        note.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        note.Modulate = UITheme.CampusSubtleText;
        layout.AddChild(note);
        layout.AddChild(new HSeparator());

        _container = MakeVBox(10);
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
            _container.AddChild(MakeStubLabel("Select a save slot to see the school seats."));
            return;
        }

        var seats = new List<Building>();
        foreach (var t in BuildingDatabase.LoadAll())
        {
            if (t.IsSchoolSeat)
            {
                seats.Add(t);
            }
        }
        if (seats.Count == 0)
        {
            _container.AddChild(MakeStubLabel("No school seats are known yet."));
            return;
        }
        seats.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        foreach (var t in seats)
        {
            _container.AddChild(BuildSeatCard(t, save));
        }
    }

    private Control BuildSeatCard(Building template, GuildSaveData save)
    {
        string school = template.SchoolAffinity;
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

        int tier = SchoolSeats.Tier(save, template.Id);
        int top = TopFacetGate(template);
        int held = Facets.Count(save, school);
        var title = new Label
        {
            Text = $"{template.Name}  ·  {school}  ·  "
                 + (tier > 0 ? $"tier {tier} / {template.MaxTier}" : "not raised")
                 + $"  ·  facets {held} / {top}",
        };
        title.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
        title.AddThemeColorOverride("font_color", UITheme.TextPrimary);
        col.AddChild(title);

        // Facets: what is held and where the rest are.
        foreach (var source in Facets.SourcesFor(school))
        {
            bool has = Facets.Has(save, school, source);
            AddLine(col, (has ? "Held: " : "Missing: ") + Facets.SourceHint(school, source),
                has ? UITheme.Gold : UITheme.TextDim);
        }
        if (Facets.SourcesFor(school).Length == 0)
        {
            AddLine(col, "This school holds no fragment of its own; its facets come from the whole guild's.", UITheme.TextDim);
        }

        // The next tier's gate, in the construction path's own words.
        if (tier < template.MaxTier)
        {
            string why = CampusConstruction.CannotBuildReason(save, template.Id);
            AddLine(col, why == null
                    ? "The next tier can be raised now, from the campus build list."
                    : $"Next tier: {why}",
                UITheme.TextPrimary);
        }

        // Doctrine state.
        var doctrineTier = Charters.DoctrineTier(template);
        if (doctrineTier != null && tier >= doctrineTier.Tier)
        {
            var entry = Charters.EntryFor(save, template.Id);
            var active = entry != null ? Charters.Doctrine(template.Id, entry.DoctrineId) : null;
            AddLine(col, active == null
                    ? "No doctrine chartered. Choose one in the Grand Hall's charters."
                    : CampusBlight.IsBlighted(save, template.Id)
                        ? $"{active.Name} is dark: the ground under the seat is blighted."
                    : Charters.IsRefitting(save, template.Id)
                        ? $"{active.Name} holds from the next moon."
                        : $"In force: {active.Name}.",
                UITheme.TextPrimary);
        }

        // The seat's own controls.
        if (template.Id == SchoolSeats.CrucibleId && tier >= 1)
        {
            if (SchoolSeats.IsWildStorm(save))
            {
                // Blighted ground (§11c): the storm will not be steered.
                AddLine(col,
                    $"The Crucible stands on blighted ground and the storm has slipped its leash. This moon's " +
                    $"front is {SchoolSeats.WildFront(save)}, and every Elementalist opens a fight with " +
                    $"{SchoolSeats.BlightedFrontCharges} of it. Its doctrine is dark. Cleanse the land to steer it again.",
                    UITheme.CampusBlightText);
            }
            else
            {
                col.AddChild(BuildFrontPicker(save));
            }
        }
        return card;
    }

    /// <summary>The Crucible's front: the element every Elementalist opens a fight in.</summary>
    private Control BuildFrontPicker(GuildSaveData save)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        var lbl = new Label { Text = "Front (this timeline):" };
        lbl.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        row.AddChild(lbl);

        var options = new ElementTag?[] { null, ElementTag.Fire, ElementTag.Ice, ElementTag.Storm, ElementTag.Earth };
        var picker = new OptionButton { CustomMinimumSize = new Vector2(160, 30) };
        picker.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        var current = SchoolSeats.Front(save);
        int selected = 0;
        for (int i = 0; i < options.Length; i++)
        {
            picker.AddItem(options[i].HasValue ? options[i].Value.ToString() : "None", i);
            if (options[i] == current)
            {
                selected = i;
            }
        }
        picker.Select(selected);
        picker.ItemSelected += idx =>
        {
            if (idx >= 0 && idx < options.Length)
            {
                SchoolSeats.SetFront(Ctx?.Save, options[(int)idx]);
            }
        };
        row.AddChild(picker);
        return row;
    }

    /// <summary>The highest facet gate on a seat's tiers (the "of N" readout).</summary>
    private static int TopFacetGate(Building template)
    {
        int top = 0;
        foreach (var t in template.Tiers)
        {
            if (t.RequiredFacets > top)
            {
                top = t.RequiredFacets;
            }
        }
        return top;
    }

    private static void AddLine(VBoxContainer parent, string text, Color color)
    {
        var lbl = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        lbl.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        lbl.AddThemeColorOverride("font_color", color);
        parent.AddChild(lbl);
    }
}
