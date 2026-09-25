using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// ForcesScreen.cs: the roster, force by force (2026-09-24)
//
// Purpose:        Asked for: "a place you can see all members assigned to a
//                 party, their equipment, their stats". Three columns. Left,
//                 every force (castle crew, each party, each detachment, the
//                 campus bench, and the people who are elsewhere: at court,
//                 overseeing, in the gaol). Middle, the members of the chosen
//                 force. Right, the chosen member: a turning figure, stats,
//                 loyalty and perk, the three equipment slots with equip and
//                 unequip, and what they can be moved to.
//
//                 Where the truth is: CompanionWhereabouts for who is where,
//                 Armory.Loadouts (keyed by companion id, so equipment follows
//                 the person into any force) for what they carry, Companion
//                 for the numbers. This screen stores nothing.
//
//                 Moves are the one thing it DOES: campus, crew and a free
//                 party trade people here, under the same rules the party
//                 picker enforces (MaxPartySize per party, the crew is the
//                 active party). A detachment, an envoy, an overseer and a
//                 prisoner cannot be moved from here, and the reason is on
//                 the button.
// Layer:          UI
// Collaborators:  StrategicView (host), CompanionWhereabouts, CompanionFigure,
//                 CompanionRoster, ExpeditionAnchors, ArmoryData, ItemDatabase,
//                 CompanionPerks, CrewStations
// ============================================================

public partial class ForcesScreen : Control
{
    // ── Hosting ────────────────────────────────────────────────────────────

    /// <summary>Open the screen over a host. <paramref name="onChanged"/> is
    /// called after any move or equipment change so the map can refresh its
    /// figures and roster; <paramref name="onClose"/> when it goes.</summary>
    public static ForcesScreen Open(Node host, Action onChanged, Action onClose, string initialForceId = null)
    {
        var layer = new CanvasLayer { Name = "ForcesUI", Layer = 60 };
        host.AddChild(layer);
        var screen = new ForcesScreen
        {
            _layer = layer,
            _onChanged = onChanged,
            _onClose = onClose,
            _forceKey = initialForceId,
        };
        screen.SetAnchorsPreset(LayoutPreset.FullRect);
        screen.MouseFilter = MouseFilterEnum.Stop;
        layer.AddChild(screen);
        screen.Rebuild();
        return screen;
    }

    private CanvasLayer _layer;
    private Action _onChanged;
    private Action _onClose;

    /// <summary>Which force is shown: "castle", a FieldParty.Id, "campus",
    /// or "elsewhere".</summary>
    private string _forceKey;
    private string _memberId;

    private HBoxContainer _columns;
    private Node3D _figureRoot;

    private const string CastleKey = "castle";
    private const string CampusKey = "campus";
    private const string ElsewhereKey = "elsewhere";

    public override void _Process(double delta)
    {
        if (_figureRoot != null && IsInstanceValid(_figureRoot))
        {
            _figureRoot.RotateY((float)delta * 0.6f);
        }
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e.IsActionPressed("ui_cancel"))
        {
            Close();
            GetViewport().SetInputAsHandled();
        }
    }

    private void Close()
    {
        _onClose?.Invoke();
        _layer?.QueueFree();
    }

    private void Changed()
    {
        SaveManager.MarkDirty();
        SaveManager.SaveIfDirty();
        _onChanged?.Invoke();
        Rebuild();
    }

    // ── Layout ─────────────────────────────────────────────────────────────

    private void Rebuild()
    {
        foreach (var child in GetChildren())
        {
            RemoveChild(child);
            child.QueueFree();
        }
        _figureRoot = null;

        var cycle = SaveManager.ActiveSave?.Cycle;
        if (cycle == null)
        {
            return;
        }

        var backdrop = new ColorRect { Color = new Color(0.02f, 0.0f, 0.04f, 0.88f) };
        backdrop.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(backdrop);

        var frame = new MarginContainer();
        frame.SetAnchorsPreset(LayoutPreset.FullRect);
        frame.OffsetTop = HudManager.BarHeight + 12;
        frame.AddThemeConstantOverride("margin_left", 28);
        frame.AddThemeConstantOverride("margin_right", 28);
        frame.AddThemeConstantOverride("margin_bottom", 20);
        AddChild(frame);

        var outer = new VBoxContainer();
        outer.AddThemeConstantOverride("separation", 10);
        frame.AddChild(outer);

        var header = new HBoxContainer();
        outer.AddChild(header);
        var title = new Label { Text = "Forces", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        title.AddThemeFontSizeOverride("font_size", UITheme.FontSizeMedium + 4);
        title.AddThemeColorOverride("font_color", UITheme.Gold);
        header.AddChild(title);
        var close = new Button { Text = "Close", CustomMinimumSize = new Vector2(120, 38) };
        UITheme.ApplyButtonStyle(close, isPrimary: false);
        close.Pressed += Close;
        header.AddChild(close);

        _columns = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        _columns.AddThemeConstantOverride("separation", 14);
        outer.AddChild(_columns);

        var forces = ForceList(cycle);
        if (string.IsNullOrEmpty(_forceKey) || !forces.Exists(f => f.key == _forceKey))
        {
            _forceKey = forces.Count > 0 ? forces[0].key : CampusKey;
        }
        var members = MembersOf(cycle, _forceKey);
        if (string.IsNullOrEmpty(_memberId) || !members.Exists(w => w.Companion.Id == _memberId))
        {
            _memberId = members.Count > 0 ? members[0].Companion.Id : null;
        }

        _columns.AddChild(Column(300, "FORCES", BuildForcesColumn(cycle, forces)));
        _columns.AddChild(Column(360, ForceTitle(cycle, _forceKey).ToUpperInvariant(), BuildMembersColumn(cycle, members)));
        var chosen = members.Find(w => w.Companion.Id == _memberId);
        _columns.AddChild(Column(0, chosen != null ? chosen.Companion.Name.ToUpperInvariant() : "", BuildDetailColumn(cycle, chosen), expand: true));
    }

    private static Control Column(int width, string heading, Control body, bool expand = false)
    {
        var panel = new PanelContainer
        {
            CustomMinimumSize = new Vector2(width, 0),
            SizeFlagsHorizontal = expand ? SizeFlags.ExpandFill : SizeFlags.Fill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        panel.AddThemeStyleboxOverride("panel", UITheme.MakePanelStyle(UITheme.BgBase, UITheme.CampusTitleBarBorder));
        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 12);
        margin.AddThemeConstantOverride("margin_right", 12);
        margin.AddThemeConstantOverride("margin_top", 10);
        margin.AddThemeConstantOverride("margin_bottom", 10);
        panel.AddChild(margin);
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 6);
        margin.AddChild(col);
        var head = new Label { Text = heading };
        head.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize - 4);
        head.AddThemeColorOverride("font_color", UITheme.TextDim);
        col.AddChild(head);
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        col.AddChild(scroll);
        body.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(body);
        return panel;
    }

    // ── Forces column ──────────────────────────────────────────────────────

    private List<(string key, string name, string status, Color accent)> ForceList(CycleState cycle)
    {
        var list = new List<(string, string, string, Color)>();
        var all = CompanionWhereabouts.Resolve(cycle);
        int crew = all.FindAll(w => w.Station == Station.Crew).Count;
        string castleState = cycle.CastleSortie != null && cycle.CastleSortie.IsLive() ? "in the field"
                           : cycle.CastleRepairLunations > 0 ? $"resupply {cycle.CastleRepairLunations}"
                           : cycle.CastleParked ? "parked" : "out";
        list.Add((CastleKey, "Castle crew", $"{crew} aboard  ·  {castleState}", UITheme.ArcaneBlue));

        if (cycle.FieldParties != null)
        {
            foreach (var p in cycle.FieldParties)
            {
                if (p == null || FieldPostings.IsDetachment(p))
                {
                    continue;
                }
                int n = p.MemberCompanionIds?.Count ?? 0;
                list.Add((p.Id, p.Name, $"{n} / {Math.Max(1, cycle.MaxPartySize)}  ·  {PartyState(cycle, p)}", UITheme.Gold));
                foreach (var det in FieldPostings.DetachmentsOf(cycle, p))
                {
                    int dn = det.MemberCompanionIds?.Count ?? 0;
                    string doing = det.State == FieldPartyState.Working ? FieldPostings.Describe(cycle, det) : "awaiting collection";
                    list.Add((det.Id, "   " + det.Name, $"{dn} posted  ·  {doing}", UITheme.GoldDim));
                }
            }
        }
        int bench = all.FindAll(w => w.Station == Station.Campus).Count;
        list.Add((CampusKey, "Campus", $"{bench} at the guild", UITheme.Violet));
        int away = all.FindAll(w => w.Station == Station.Envoy || w.Station == Station.Overseer || w.Station == Station.Imprisoned).Count;
        if (away > 0)
        {
            list.Add((ElsewhereKey, "Elsewhere", $"{away} at court, overseeing, or held", UITheme.TextSecondary));
        }
        return list;
    }

    private static string PartyState(CycleState cycle, FieldParty p)
    {
        if (p.X < 0)
        {
            return "not sited";
        }
        if (p.Sortie != null && p.Sortie.IsLive())
        {
            return "in the field";
        }
        if (p.State == FieldPartyState.Travelling)
        {
            return "on the road";
        }
        if (p.State == FieldPartyState.Working)
        {
            return FieldPostings.Describe(cycle, p);
        }
        if (WorldClock.PartyBusy(cycle, p))
        {
            return "returning";
        }
        return "free";
    }

    private Control BuildForcesColumn(CycleState cycle, List<(string key, string name, string status, Color accent)> forces)
    {
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 4);
        foreach (var f in forces)
        {
            string key = f.key;
            col.AddChild(RowButton(f.name, f.status, f.accent, key == _forceKey, () =>
            {
                _forceKey = key;
                _memberId = null;
                Rebuild();
            }));
        }
        return col;
    }

    // ── Members column ─────────────────────────────────────────────────────

    private List<Whereabout> MembersOf(CycleState cycle, string key)
    {
        var all = CompanionWhereabouts.Resolve(cycle);
        var list = new List<Whereabout>();
        foreach (var w in all)
        {
            bool match = key switch
            {
                CastleKey => w.Station == Station.Crew,
                CampusKey => w.Station == Station.Campus,
                ElsewhereKey => w.Station == Station.Envoy || w.Station == Station.Overseer || w.Station == Station.Imprisoned,
                _ => w.Station != Station.Imprisoned && w.PieceId == key
                     && (w.Station == Station.FieldParty || w.Station == Station.Detachment
                         || w.Station == Station.Envoy || w.Station == Station.Overseer),
            };
            if (match)
            {
                list.Add(w);
            }
        }
        return list;
    }

    private string ForceTitle(CycleState cycle, string key)
    {
        switch (key)
        {
            case CastleKey: return "Castle crew";
            case CampusKey: return "Campus";
            case ElsewhereKey: return "Elsewhere";
        }
        var p = cycle.FieldParties?.Find(x => x != null && x.Id == key);
        return p?.Name ?? "Force";
    }

    private Control BuildMembersColumn(CycleState cycle, List<Whereabout> members)
    {
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 4);
        if (members.Count == 0)
        {
            var none = new Label { Text = "Nobody here.", AutowrapMode = TextServer.AutowrapMode.WordSmart };
            none.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
            none.AddThemeColorOverride("font_color", UITheme.TextDim);
            col.AddChild(none);
            return col;
        }
        foreach (var w in members)
        {
            var c = w.Companion;
            string id = c.Id;
            string voc = Vocations.Describe(c);
            string line = $"{c.UnitClass} · {c.School} · {c.PersonalityTrait}" + (string.IsNullOrEmpty(voc) ? "" : $" · {voc}");
            if (w.CrewStation.HasValue)
            {
                line = $"{CompanionWhereabouts.StationName(w.CrewStation.Value)}  ·  " + line;
            }
            if (!string.IsNullOrEmpty(w.Detail) && _forceKey == ElsewhereKey)
            {
                line = $"{w.Place}  ·  {w.Detail}";
            }
            else if (w.Injured)
            {
                line += $"  ·  injured {c.InjuredLunationsRemaining}";
            }
            col.AddChild(RowButton(c.Name, line, CompanionWhereabouts.TintFor(c), id == _memberId, () =>
            {
                _memberId = id;
                Rebuild();
            }));
        }
        return col;
    }

    private static Button RowButton(string name, string status, Color accent, bool selected, Action onPressed)
    {
        var btn = new Button
        {
            CustomMinimumSize = new Vector2(0, 44),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        var style = UITheme.MakePanelStyle(selected ? UITheme.BgCard : UITheme.BgRaised, selected ? accent : UITheme.NeutralDim);
        style.BorderWidthLeft = 4;
        style.BorderColor = accent;
        btn.AddThemeStyleboxOverride("normal", style);
        btn.AddThemeStyleboxOverride("hover", style);
        btn.AddThemeStyleboxOverride("pressed", style);
        btn.Pressed += () => onPressed();

        var box = new VBoxContainer
        {
            MouseFilter = MouseFilterEnum.Ignore,
            AnchorRight = 1f,
            AnchorBottom = 1f,
            OffsetLeft = 12,
            OffsetTop = 4,
            OffsetRight = -6,
            OffsetBottom = -4,
        };
        box.AddThemeConstantOverride("separation", 0);
        btn.AddChild(box);
        var n = new Label { Text = name, MouseFilter = MouseFilterEnum.Ignore };
        n.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize - 2);
        n.AddThemeColorOverride("font_color", selected ? accent : UITheme.TextPrimary);
        box.AddChild(n);
        var s = new Label { Text = status, MouseFilter = MouseFilterEnum.Ignore, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        s.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
        s.AddThemeColorOverride("font_color", UITheme.TextSecondary);
        box.AddChild(s);
        return btn;
    }

    // ── Detail column ──────────────────────────────────────────────────────

    private Control BuildDetailColumn(CycleState cycle, Whereabout w)
    {
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 10);
        if (w == null)
        {
            var none = new Label { Text = "Choose someone." };
            none.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
            none.AddThemeColorOverride("font_color", UITheme.TextDim);
            col.AddChild(none);
            return col;
        }
        var c = w.Companion;
        var save = SaveManager.ActiveSave;

        // Top: the figure beside the summary.
        var top = new HBoxContainer();
        top.AddThemeConstantOverride("separation", 14);
        col.AddChild(top);
        top.AddChild(BuildFigureView(c));

        var summary = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        summary.AddThemeConstantOverride("separation", 4);
        top.AddChild(summary);
        AddLine(summary, $"{c.UnitClass} · {c.School} · {c.PersonalityTrait}", UITheme.TextSecondary);
        AddLine(summary, $"Where: {w.Place}" + (string.IsNullOrEmpty(w.Detail) ? "" : $"  ·  {w.Detail}"),
                w.Injured ? UITheme.Warning : UITheme.TextSecondary);
        AddLine(summary, "", UITheme.TextDim);
        AddStat(summary, "Loyalty", $"{c.Loyalty}  ({c.GetLoyaltyTier()})");
        AddStat(summary, "Arc", c.ArcStage >= 4 ? "complete" : c.ArcStage == 0 ? "not begun" : $"stage {c.ArcStage} of 4");
        AddStat(summary, "Perk", PerkLine(c));
        AddStat(summary, "Vocation", VocationLine(c));
        AddLine(summary, "", UITheme.TextDim);
        if (c.UnitClass == "Arcane")
        {
            AddStat(summary, "HP", c.BaseHP.ToString());
            AddStat(summary, "Mana", c.BaseMana.ToString());
            AddStat(summary, "Speed", c.BaseSpeed.ToString());
        }
        else
        {
            AddStat(summary, "HP", c.BaseHP.ToString());
            AddStat(summary, "Damage", c.BaseAttackDamage.ToString());
            AddStat(summary, "Range", c.BaseAttackRange.ToString());
            AddStat(summary, "Speed", c.BaseSpeed.ToString());
            AddStat(summary, "Armor", c.BaseArmor.ToString());
            AddStat(summary, "Actions", c.BaseActionPoints.ToString());
        }
        if (c.ExpeditionHP == 0)
        {
            AddLine(summary, "Downed this expedition: cannot be fielded again until home.", UITheme.Warning);
        }
        if (c.TrainedStanceIds != null && c.TrainedStanceIds.Count > 0)
        {
            AddStat(summary, "Stances", string.Join(", ", c.TrainedStanceIds)
                + (string.IsNullOrEmpty(c.SignatureStanceId) ? "" : $"  (signature: {c.SignatureStanceId})"));
        }
        if (c.ContributedCardIds != null && c.ContributedCardIds.Count > 0)
        {
            AddStat(summary, "Cards", $"{c.ContributedCardIds.Count} contributed to the deck");
        }

        // Equipment.
        col.AddChild(new HSeparator());
        AddLine(col, "EQUIPMENT", UITheme.TextDim);
        var armory = save?.Armory;
        if (armory == null)
        {
            AddLine(col, "No armory.", UITheme.TextDim);
        }
        else
        {
            var slots = new HBoxContainer();
            slots.AddThemeConstantOverride("separation", 8);
            col.AddChild(slots);
            var equipped = armory.GetEquipped(c.Id);
            foreach (EquipmentSlot slot in Enum.GetValues(typeof(EquipmentSlot)))
            {
                ItemInstance item = null;
                foreach (var (s, it) in equipped)
                {
                    if (s == slot)
                    {
                        item = it;
                        break;
                    }
                }
                slots.AddChild(BuildSlotCard(c, slot, item, armory));
            }

            // What could be equipped: unequipped items that fit the class.
            var fits = new List<ItemInstance>();
            foreach (var it in armory.GetUnequipped())
            {
                var def = ItemDatabase.Get(it.DefinitionId);
                if (def == null || def.IsConsumable || !ClassFits(c, it.UnitClass))
                {
                    continue;
                }
                fits.Add(it);
            }
            if (fits.Count > 0)
            {
                AddLine(col, "In the armory:", UITheme.TextSecondary);
                foreach (var it in fits)
                {
                    col.AddChild(BuildArmoryRow(c, it, armory));
                }
            }
            else
            {
                AddLine(col, "Nothing in the armory fits them.", UITheme.TextDim);
            }
        }

        // Moves.
        col.AddChild(new HSeparator());
        AddLine(col, "ASSIGN", UITheme.TextDim);
        col.AddChild(BuildMoveRow(cycle, save, w));
        return col;
    }

    // ── Figure ─────────────────────────────────────────────────────────────

    /// <summary>A small 3D view of the person: the shared rig when it exists,
    /// the stand-in until then, turning slowly. Its own world, so the map's
    /// lighting and fog do not reach into the panel.</summary>
    private Control BuildFigureView(Companion c)
    {
        var container = new SubViewportContainer
        {
            Stretch = true,
            CustomMinimumSize = new Vector2(220, 300),
        };
        var viewport = new SubViewport
        {
            OwnWorld3D = true,
            TransparentBg = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            Size = new Vector2I(220, 300),
        };
        container.AddChild(viewport);

        var world = new Node3D();
        viewport.AddChild(world);

        var sun = new DirectionalLight3D
        {
            LightEnergy = 1.2f,
            RotationDegrees = new Vector3(-40f, 35f, 0f),
        };
        world.AddChild(sun);
        var fill = new OmniLight3D
        {
            LightEnergy = 0.6f,
            OmniRange = 8f,
            Position = new Vector3(-1.5f, 1.2f, 2f),
            LightColor = new Color(0.75f, 0.7f, 0.95f),
        };
        world.AddChild(fill);

        _figureRoot = new Node3D();
        world.AddChild(_figureRoot);
        var fig = CompanionFigure.Build(CompanionWhereabouts.TintFor(c), c.IsInjured, scale: 1f);
        _figureRoot.AddChild(fig);

        var cam = new Camera3D { Fov = 32f, Current = true };
        Vector3 at = new Vector3(0f, 1.05f, 3.6f);
        Vector3 target = new Vector3(0f, 0.9f, 0f);
        cam.Position = at;
        cam.Basis = Basis.LookingAt(target - at, Vector3.Up);
        world.AddChild(cam);
        return container;
    }

    // ── Equipment ──────────────────────────────────────────────────────────

    private Control BuildSlotCard(Companion c, EquipmentSlot slot, ItemInstance item, ArmoryData armory)
    {
        var panel = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 96) };
        var def = item != null ? ItemDatabase.Get(item.DefinitionId) : null;
        Color edge = item != null ? UITheme.RarityColor(item.Rarity) : UITheme.NeutralDim;
        panel.AddThemeStyleboxOverride("panel", UITheme.MakePanelStyle(UITheme.BgRaised, edge));
        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 8);
        margin.AddThemeConstantOverride("margin_right", 8);
        margin.AddThemeConstantOverride("margin_top", 6);
        margin.AddThemeConstantOverride("margin_bottom", 6);
        panel.AddChild(margin);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 2);
        margin.AddChild(box);
        AddLine(box, slot.ToString().ToUpperInvariant(), UITheme.TextDim);
        if (item == null)
        {
            AddLine(box, "empty", UITheme.TextDim);
            return panel;
        }
        AddLine(box, item.Name, edge);
        AddLine(box, def != null ? StatSummary(def) : "", UITheme.TextSecondary);
        var un = new Button { Text = "Unequip", CustomMinimumSize = new Vector2(0, 30) };
        UITheme.ApplyButtonStyle(un, isPrimary: false);
        string cid = c.Id;
        un.Pressed += () =>
        {
            armory.Unequip(cid, slot);
            Changed();
        };
        box.AddChild(un);
        return panel;
    }

    private Control BuildArmoryRow(Companion c, ItemInstance item, ArmoryData armory)
    {
        var def = ItemDatabase.Get(item.DefinitionId);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        var text = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        text.AddThemeConstantOverride("separation", 0);
        row.AddChild(text);
        AddLine(text, $"{item.Name}  [{item.Slot}]  [{item.UnitClass}]", UITheme.RarityColor(item.Rarity));
        AddLine(text, def != null ? StatSummary(def) : "", UITheme.TextSecondary);

        bool occupied = false;
        if (Enum.TryParse<EquipmentSlot>(item.Slot, out var slot))
        {
            occupied = !string.IsNullOrEmpty(armory.GetLoadout(c.Id).GetSlot(slot));
        }
        var eq = new Button { Text = occupied ? "Swap in" : "Equip", CustomMinimumSize = new Vector2(96, 30) };
        UITheme.ApplyButtonStyle(eq, isPrimary: true);
        string cid = c.Id;
        string iid = item.InstanceId;
        eq.Pressed += () =>
        {
            if (Enum.TryParse<EquipmentSlot>(item.Slot, out var s))
            {
                armory.Unequip(cid, s);
            }
            armory.Equip(cid, iid);
            Changed();
        };
        row.AddChild(eq);
        return row;
    }

    private static bool ClassFits(Companion c, string itemClass)
    {
        switch (itemClass)
        {
            case "Wizard": return c.UnitClass == "Arcane";
            case "Martial": return c.UnitClass == "Fighter" || c.UnitClass == "Ranger";
            default: return true;
        }
    }

    private static string StatSummary(ItemDefinition def)
    {
        var parts = new List<string>();
        var s = def.Stats;
        if (s != null)
        {
            if (s.MaxHP != 0) parts.Add($"{Signed(s.MaxHP)} HP");
            if (s.MaxMana != 0) parts.Add($"{Signed(s.MaxMana)} Mana");
            if (s.Armor != 0) parts.Add($"{Signed(s.Armor)} Armor");
            if (s.BaseSpeed != 0) parts.Add($"{Signed(s.BaseSpeed)} Speed");
            if (s.AttackDamage != 0) parts.Add($"{Signed(s.AttackDamage)} Dmg");
            if (s.AttackRange != 0) parts.Add($"{Signed(s.AttackRange)} Rng");
            if (s.SpellDamage != 0) parts.Add($"{Signed(s.SpellDamage)} Spell");
        }
        if (!string.IsNullOrEmpty(def.Passive) && def.Passive != "None")
        {
            parts.Add(def.PassiveValue != 0 ? $"{def.Passive} {def.PassiveValue}" : def.Passive);
        }
        return parts.Count == 0 ? "No bonuses" : string.Join(" · ", parts);
    }

    private static string Signed(int v) => v > 0 ? $"+{v}" : v.ToString();

    /// <summary>"Journeyman Scout, 6 moons worked, Master at 10: what they do".</summary>
    private static string VocationLine(Companion c)
    {
        int r = Vocations.RankOf(c);
        if (r == 0)
        {
            return "none";
        }
        string next = r >= Vocations.MaxRank ? ""
                    : r == 2 ? $", Master at {Vocations.Rank3Moons}"
                    : $", Journeyman at {Vocations.Rank2Moons}";
        string kind = Vocations.PostingKindOf(c.Vocation);
        string effect = Vocations.EffectLine(c.Vocation, r, string.IsNullOrEmpty(kind) ? FieldPostings.Garrison : kind);
        return $"{Vocations.Describe(c)}, {c.VocationMoons} moon(s) worked{next}. {effect}.";
    }

    private static string PerkLine(Companion c)
    {
        string name = c.PersonalityTrait switch
        {
            "Stoic" => "Unshakeable (+1 Armor)",
            "Reckless" => "First Blood (+1 Damage)",
            "Curious" => "Pathfinder's Eye (+1 Move)",
            "Loyal" => "Shoulder to Shoulder (+2 party pool)",
            "Cunning" => "Finder's Fee (+10 gold at extraction)",
            _ => "none",
        };
        if (name == "none")
        {
            return name;
        }
        return CompanionPerks.PerkActive(c) ? $"{name}, active" : $"{name}, needs Trusted";
    }

    // ── Moves ──────────────────────────────────────────────────────────────

    private Control BuildMoveRow(CycleState cycle, GuildSaveData save, Whereabout w)
    {
        var row = new VBoxContainer();
        row.AddThemeConstantOverride("separation", 4);
        var c = w.Companion;

        string blocked = MoveBlockedReason(cycle, w);
        if (blocked != null)
        {
            AddLine(row, blocked, UITheme.TextDim);
            return row;
        }

        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 8);
        row.AddChild(buttons);

        if (w.Station != Station.Campus)
        {
            AddMoveButton(buttons, "To campus", true, "", () =>
            {
                LeaveCurrent(cycle, save, c);
                Changed();
            });
        }
        if (w.Station != Station.Crew)
        {
            bool castleFree = cycle.CastleSortie == null || !cycle.CastleSortie.IsLive();
            bool room = (save.ActivePartyCompanionIds?.Count ?? 0) < Math.Max(1, save.MaxPartySize);
            string why = !castleFree ? "the castle is in the field" : !room ? $"crew is full ({save.MaxPartySize})" : "";
            AddMoveButton(buttons, "To castle crew", castleFree && room, why, () =>
            {
                LeaveCurrent(cycle, save, c);
                if (CompanionRoster.TryAddToParty(c.Id))
                {
                    c.Posting = CompanionPosting.Crew;
                    c.FieldPartyId = "";
                }
                ExpeditionAnchors.ReconcilePostings(cycle);
                Changed();
            });
        }
        if (cycle.FieldParties != null)
        {
            foreach (var p in cycle.FieldParties)
            {
                if (p == null || FieldPostings.IsDetachment(p) || p.Id == w.PieceId)
                {
                    continue;
                }
                string state = PartyState(cycle, p);
                bool free = state == "free";
                bool room = (p.MemberCompanionIds?.Count ?? 0) < Math.Max(1, cycle.MaxPartySize);
                string why = !free ? $"{p.Name} is {state}" : !room ? $"{p.Name} is full ({cycle.MaxPartySize})" : "";
                var target = p;
                AddMoveButton(buttons, $"To {p.Name}", free && room, why, () =>
                {
                    LeaveCurrent(cycle, save, c);
                    c.Posting = CompanionPosting.Field;
                    c.FieldPartyId = target.Id;
                    ExpeditionAnchors.ReconcilePostings(cycle);
                    Changed();
                });
            }
        }
        return row;
    }

    /// <summary>Why this person cannot be reassigned from here, or null.</summary>
    private string MoveBlockedReason(CycleState cycle, Whereabout w)
    {
        switch (w.Station)
        {
            case Station.Imprisoned: return "Held in a gaol. Walk a party to the prison to free them.";
            case Station.Envoy: return "On a mission at court. Recall them through the council, or stop the posting.";
            case Station.Overseer: return "Overseeing a depot with a garrison. Stop the garrison to release them.";
            case Station.Detachment: return "Posted with a detachment. Collect the detachment first.";
        }
        if (w.Station == Station.FieldParty)
        {
            var p = cycle.FieldParties?.Find(x => x != null && x.Id == w.PieceId);
            if (p != null)
            {
                string state = PartyState(cycle, p);
                if (state != "free")
                {
                    return $"{p.Name} is {state}; nobody leaves it until it is standing free.";
                }
            }
        }
        if (w.Station == Station.Crew && cycle.CastleSortie != null && cycle.CastleSortie.IsLive())
        {
            return "The castle is in the field; the crew stays aboard until it is home or camped.";
        }
        return null;
    }

    /// <summary>Take the person off whatever they are on now. The crew is the
    /// active party, so leaving it is a roster removal as well as a posting.</summary>
    private static void LeaveCurrent(CycleState cycle, GuildSaveData save, Companion c)
    {
        if (save.ActivePartyCompanionIds != null && save.ActivePartyCompanionIds.Contains(c.Id))
        {
            CompanionRoster.RemoveFromParty(c.Id);
        }
        c.Posting = CompanionPosting.Campus;
        c.FieldPartyId = "";
        ExpeditionAnchors.ReconcilePostings(cycle);
    }

    private static void AddMoveButton(HBoxContainer row, string text, bool enabled, string why, Action onPressed)
    {
        var btn = new Button
        {
            Text = text,
            Disabled = !enabled,
            TooltipText = enabled ? "" : why,
            CustomMinimumSize = new Vector2(0, 34),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        UITheme.ApplyButtonStyle(btn, isPrimary: false);
        btn.Pressed += () => onPressed();
        row.AddChild(btn);
    }

    // ── Small helpers ──────────────────────────────────────────────────────

    private static void AddLine(VBoxContainer parent, string text, Color color)
    {
        var l = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        l.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
        l.AddThemeColorOverride("font_color", color);
        parent.AddChild(l);
    }

    private static void AddStat(VBoxContainer parent, string label, string value)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        var l = new Label { Text = label, CustomMinimumSize = new Vector2(80, 0) };
        l.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
        l.AddThemeColorOverride("font_color", UITheme.TextDim);
        row.AddChild(l);
        var v = new Label { Text = value, SizeFlagsHorizontal = SizeFlags.ExpandFill, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        v.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
        v.AddThemeColorOverride("font_color", UITheme.TextPrimary);
        row.AddChild(v);
        parent.AddChild(row);
    }
}
