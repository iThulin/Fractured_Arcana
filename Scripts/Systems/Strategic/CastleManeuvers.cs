using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// CastleManeuvers.cs
//
// Purpose:        The castle's maneuvers (docs/provinces_v1_9_bastion.md,
//                 slice PV4; P4, X1, X7). The font is the castle: spells
//                 the guild knows are cast from it, on the strategic map,
//                 into its province and the provinces beside it (one more
//                 ring around a forward camp). Each maneuver is a spell of
//                 the grimoire given a second use at the castle (PR70):
//                 Force Path, Verdant Passage, Veil, Retrace, Deploy
//                 Waystation, Clockwork Skimmer, Ley Leap (Ley Tap),
//                 Campward, Wayfarer's Beacon, Thornwall and Stasis Snare.
//                 The font has its own Essence, full at each new moon
//                 (PR71); a castle in a province held for its benefits
//                 casts for 1 less, never below 1, as PR46 has it.
// Layer:          System (strategic)
// Collaborators:  CastleManeuverState.cs (the state), CastleHops.cs (the
//                 march reads Force Path and Verdant Passage),
//                 CastleMarch.cs (Execute arms the camp and records the
//                 march), CastleThreats.cs (a warded camp),
//                 KingdomTickSimulation.cs (a front held still),
//                 ExpeditionAnchors.cs (ParkCastleAt), GrimoireState.cs
//                 and OverworldSpellRegistry.cs (what the guild knows),
//                 StrategicView.Bastion.cs (the Maneuvers dialog),
//                 StrategicView.cs (NewMoon), StrategicDebug.cs
//                 (DebugLearnAll), ProvinceSaveAssert.Bastion.cs
// See:            docs/provinces_v1_9_bastion.md section 5
// ============================================================

/// <summary>What a maneuver is aimed at.</summary>
public enum ManeuverTarget
{
    None = 0,
    /// <summary>A province within the font's reach.</summary>
    ReachProvince = 1,
    /// <summary>A province beside the castle's.</summary>
    NeighbourProvince = 2,
    /// <summary>A province the guild holds within LeapHops of the castle's.</summary>
    HeldProvince = 3,
    /// <summary>The front contesting the castle's province.</summary>
    CastleFront = 4,
    /// <summary>A front contesting a province within the font's reach.</summary>
    ReachFront = 5,
}

/// <summary>One maneuver: the spell it is cast from, its Essence, its aim and its words.</summary>
public sealed class ManeuverDef
{
    public string SpellId = "";
    public string Name = "";
    public int Essence;
    public ManeuverTarget Target = ManeuverTarget.None;
    public string Text = "";
}

/// <summary>The castle's maneuvers (PV4).</summary>
public static class CastleManeuvers
{
    // ── Tuning (section 5, PR70 and PR71) ────────────────────────────────

    /// <summary>The font's Essence, full at each new moon.</summary>
    public const int FontMax = 10;

    /// <summary>How far Ley Leap reaches on the province graph.</summary>
    public const int LeapHops = 3;

    /// <summary>The staging source of a forward camp.</summary>
    public const string WaystationSource = "Waystation";

    /// <summary>Every maneuver, in the order the dialog lists them.</summary>
    public static readonly ManeuverDef[] All =
    {
        new ManeuverDef { SpellId = "force_path", Name = "Force Path", Essence = 4,
            Text = "The next march fords rivers and climbs ridges at no extra cost." },
        new ManeuverDef { SpellId = "verdant_passage", Name = "Verdant Passage", Essence = 3,
            Text = "Until the new moon, hops into woodland and wetland provinces cost as lowland." },
        new ManeuverDef { SpellId = "veil", Name = "Veil", Essence = 3,
            Text = "The next march leaves no trail: whatever hunts the castle loses it." },
        new ManeuverDef { SpellId = "retrace", Name = "Retrace", Essence = 4,
            Text = "Undo this lunation's last march: the castle stands where it began, the fuel back "
                   + "in the furnace and no camp to make. Once a lunation." },
        new ManeuverDef { SpellId = "deploy_waystation", Name = "Deploy Waystation", Essence = 5,
            Target = ManeuverTarget.NeighbourProvince,
            Text = "A forward camp in a province beside the castle's: a party can stage there, and the "
                   + "font reaches one province beyond it, until the castle next moves." },
        new ManeuverDef { SpellId = "clockwork_skimmer", Name = "Clockwork Skimmer", Essence = 3,
            Target = ManeuverTarget.ReachProvince,
            Text = "A gadget flies over a province within the font's reach and charts all its ground." },
        new ManeuverDef { SpellId = "ley_tap", Name = "Ley Leap", Essence = 6,
            Target = ManeuverTarget.HeldProvince,
            Text = "Leap along the ley to a province the guild holds within three hops: no road walked "
                   + "and no fuel, but the crews make camp as after a march." },
        new ManeuverDef { SpellId = "campward", Name = "Campward", Essence = 2,
            Text = "Ward the castle's camp, this one or the next: nothing strikes it while the crews unpack." },
        new ManeuverDef { SpellId = "wayfarers_beacon", Name = "Wayfarer's Beacon", Essence = 1,
            Text = "The next march that ends in a province the guild holds makes no camp: no unpacking, "
                   + "no exposure, and no refuel." },
        new ManeuverDef { SpellId = "thornwall", Name = "Thornwall", Essence = 3,
            Target = ManeuverTarget.CastleFront,
            Text = "The front in the castle's province stands still at the next new moon." },
        // C1 (2026-11-10): the castle's own works. Every guild has them; no spell is needed.
        new ManeuverDef { SpellId = "castle_scry", Name = "Scry Province", Essence = 2,
            Target = ManeuverTarget.ReachProvince,
            Text = "See a province within the castle's reach: every site in it becomes known, with its "
                   + "guards and its ruler's plan." },
        new ManeuverDef { SpellId = "castle_discord", Name = "Sow Discord", Essence = 4,
            Target = ManeuverTarget.ReachProvince,
            Text = "Break the plan of the realm that rules a province within reach. It holds no plan for "
                   + "the rest of this moon and all of the next, and land it was annexing stops gaining. "
                   + "A front already open keeps moving." },
        new ManeuverDef { SpellId = "castle_bombard", Name = "Bombardment", Essence = 5,
            Target = ManeuverTarget.ReachFront,
            Text = "The castle's engines pound a front within reach. Your next Seize there counts double." },
        new ManeuverDef { SpellId = "stasis_snare", Name = "Stasis Snare", Essence = 3,
            Target = ManeuverTarget.ReachFront,
            Text = "A front in a province within the font's reach stands still at the next new moon." },
    };

    /// <summary>A maneuver by its spell's id, or null.</summary>
    public static ManeuverDef Get(string spellId)
    {
        foreach (var def in All)
        {
            if (def.SpellId == spellId)
                return def;
        }
        return null;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Reading
    // ══════════════════════════════════════════════════════════════════════

    private static int Lunation(CycleState cycle) => cycle?.Calendar?.CurrentLunation ?? 0;

    /// <summary>True when the guild knows the spell: learned, or an innate of its school.</summary>
    public static bool Knows(CycleState cycle, string spellId)
    {
        if (cycle == null || string.IsNullOrEmpty(spellId))
            return false;
        if (IsCastleWork(spellId))
            return true;   // C1: the castle's own works need no spell
        if (cycle.Grimoire?.KnownSpellIds != null && cycle.Grimoire.KnownSpellIds.Contains(spellId))
            return true;
        var def = OverworldSpellRegistry.Get(spellId);
        return def != null && def.IsInnate
               && string.Equals(def.School, cycle.SelectedSchool ?? "", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>C1: a maneuver that is the castle's own work, not a spell's second use.</summary>
    public static bool IsCastleWork(string spellId)
        => !string.IsNullOrEmpty(spellId) && spellId.StartsWith("castle_", StringComparison.Ordinal);

    /// <summary>C1 (Bombardment): was this front bombarded? Spends the bombardment.</summary>
    public static bool ConsumeBombardment(CycleState cycle, string frontId)
        => cycle?.Maneuvers?.BombardedFronts != null && !string.IsNullOrEmpty(frontId)
           && cycle.Maneuvers.BombardedFronts.Remove(frontId);

    /// <summary>C1 (Scry Province): fills the province with its sites and guards if no party has
    /// been there, then makes every site in it found and known. Returns the line for the player.</summary>
    private static string ScryProvince(CycleState cycle, Province to)
    {
        var world = cycle.World;
        if (to.SeatX >= 0 && to.SeatY >= 0)
        {
            ProvinceField.Fill(world, to.SeatX, to.SeatY);
            ProvinceField.PostGuards(world, to.SeatX, to.SeatY);
        }
        int fights = 0, guards = 0, other = 0;
        foreach (var poi in world.Pois)
        {
            if (poi == null || poi.Consumed || poi.Kind == PoiKind.Concord
                || !world.InBounds(poi.X, poi.Y) || world.ProvinceIndexAt(poi.X, poi.Y) != to.Index)
            {
                continue;
            }
            poi.Discovered = true;
            poi.Known = true;
            if (poi.Guard)
                guards++;
            else if (poi.Kind == PoiKind.Combat || poi.Kind == PoiKind.Prison)
                fights++;
            else
                other++;
        }
        string plan = KingdomTickSimulation.PlanCardLine(cycle, to);
        return $"{ProvinceControl.Name(to)}: {fights} fight(s), {guards} guard(s), {other} other site(s), all known."
               + (plan.Length > 0 ? " " + plan : "");
    }

    /// <summary>The maneuvers the guild knows, in the dialog's order.</summary>
    public static List<ManeuverDef> Known(CycleState cycle)
    {
        var known = new List<ManeuverDef>();
        foreach (var def in All)
        {
            if (Knows(cycle, def.SpellId))
                known.Add(def);
        }
        return known;
    }

    /// <summary>The Essence a maneuver costs now: 1 less where the castle stands in a province
    /// held for its benefits (PR46), never below 1.</summary>
    public static int CostFor(CycleState cycle, ManeuverDef def)
    {
        if (def == null)
            return 0;
        bool held = cycle != null && ProvinceBenefits.HeldForBenefitsAt(cycle, cycle.CastleX, cycle.CastleY);
        return held ? Math.Max(1, def.Essence - 1) : def.Essence;
    }

    /// <summary>The font's Essence now.</summary>
    public static int Font(CycleState cycle) => cycle?.Maneuvers?.FontEssence ?? 0;

    /// <summary>Force Path is laid for the next march.</summary>
    public static bool ForcePathArmed(CycleState cycle) => cycle?.Maneuvers?.ForcePathArmed ?? false;

    /// <summary>Verdant Passage holds this lunation.</summary>
    public static bool VerdantActive(CycleState cycle)
        => cycle?.Maneuvers != null && cycle.Maneuvers.VerdantLunation >= 0
           && cycle.Maneuvers.VerdantLunation == Lunation(cycle);

    /// <summary>The castle's camp is warded (Campward) while its crews unpack.</summary>
    public static bool CampWarded(CycleState cycle)
        => cycle?.Maneuvers != null && cycle.CastleParked && cycle.Maneuvers.WardedCampX >= 0
           && cycle.Maneuvers.WardedCampX == cycle.CastleX && cycle.Maneuvers.WardedCampY == cycle.CastleY;

    /// <summary>The provinces within the font's reach: the castle's, the ones beside it, and
    /// the ones beside a standing forward camp.</summary>
    public static HashSet<int> Reach(CycleState cycle)
    {
        var reach = new HashSet<int>();
        var world = cycle?.World;
        int castle = CastleHops.CastleProvince(cycle);
        if (castle < 0)
            return reach;
        AddRing(world, castle, reach);
        var waystation = ProvinceControl.ProvinceById(world, cycle.Maneuvers?.WaystationProvinceId ?? "");
        if (waystation != null)
            AddRing(world, waystation.Index, reach);
        return reach;
    }

    private static void AddRing(WorldData world, int index, HashSet<int> reach)
    {
        if (index < 0 || index >= world.Provinces.Count)
            return;
        reach.Add(index);
        foreach (int q in world.Provinces[index].Neighbors)
        {
            if (q >= 0 && q < world.Provinces.Count)
                reach.Add(q);
        }
    }

    /// <summary>True when the guild holds the province for its benefits or rules it.</summary>
    private static bool Holds(CycleState cycle, Province province)
        => province != null && (ProvinceBenefits.HeldForBenefits(cycle, province)
                                || ProvinceControl.RulerOf(cycle, province) == ProvinceControl.Guild);

    /// <summary>What a maneuver can be aimed at now, as (key, label): a province's index or a
    /// front's id. Empty for a maneuver aimed at nothing.</summary>
    public static List<(string Key, string Label)> Targets(CycleState cycle, ManeuverDef def)
    {
        var targets = new List<(string Key, string Label)>();
        var world = cycle?.World;
        int castle = CastleHops.CastleProvince(cycle);
        if (def == null || castle < 0)
            return targets;
        var provinces = world.Provinces;
        switch (def.Target)
        {
            case ManeuverTarget.ReachProvince:
            {
                var reach = new List<int>(Reach(cycle));
                reach.Sort();
                foreach (int p in reach)
                    targets.Add((p.ToString(), ProvinceLabel(cycle, provinces[p])));
                break;
            }
            case ManeuverTarget.NeighbourProvince:
            {
                var ring = new List<int>(provinces[castle].Neighbors);
                ring.Sort();
                foreach (int p in ring)
                {
                    if (p >= 0 && p < provinces.Count && p != castle)
                        targets.Add((p.ToString(), ProvinceLabel(cycle, provinces[p])));
                }
                break;
            }
            case ManeuverTarget.HeldProvince:
            {
                for (int p = 0; p < provinces.Count; p++)
                {
                    if (p == castle || !Holds(cycle, provinces[p]))
                        continue;
                    int hops = CastleHops.HopsBetween(world, castle, p);
                    if (hops > 0 && hops <= LeapHops)
                        targets.Add((p.ToString(), $"{ProvinceLabel(cycle, provinces[p])}  ({hops} hop(s))"));
                }
                break;
            }
            case ManeuverTarget.CastleFront:
            case ManeuverTarget.ReachFront:
            {
                var reach = def.Target == ManeuverTarget.CastleFront ? new HashSet<int> { castle } : Reach(cycle);
                foreach (var wf in cycle.Warfronts ?? new List<Warfront>())
                {
                    if (wf == null || wf.Closed || wf.IsCacheSiege || string.IsNullOrEmpty(wf.ProvinceId))
                        continue;
                    var contested = ProvinceControl.ProvinceById(world, wf.ProvinceId);
                    if (contested == null || !reach.Contains(contested.Index))
                        continue;
                    targets.Add((wf.Id, $"The front at {ProvinceLabel(cycle, contested)}, Advance {wf.Advance}"));
                }
                break;
            }
        }
        return targets;
    }

    private static string ProvinceLabel(CycleState cycle, Province province)
    {
        string realm = ProvinceControl.RealmName(cycle, province.KingdomId);
        return string.IsNullOrEmpty(realm) ? ProvinceControl.Name(province) : $"{ProvinceControl.Name(province)}, {realm}";
    }

    /// <summary>Can the maneuver be cast now, aim aside; if not, why, in the player's words.</summary>
    public static bool CanCast(CycleState cycle, ManeuverDef def, out string why)
    {
        why = "";
        if (cycle?.World == null || def == null)
        {
            why = "There is no castle to cast from.";
            return false;
        }
        if (CastleHops.CastleProvince(cycle) < 0)
        {
            why = "The font works on the province graph, and the castle stands in no province.";
            return false;
        }
        if (!Knows(cycle, def.SpellId))
        {
            why = $"The guild does not know {def.Name}.";
            return false;
        }
        if (!cycle.CastleParked)
        {
            why = "The castle is out on a sortie. The font is cast from a castle that stands.";
            return false;
        }
        int cost = CostFor(cycle, def);
        if (Font(cycle) < cost)
        {
            why = $"The font holds {Font(cycle)} Essence and {def.Name} needs {cost}. It fills again at the new moon.";
            return false;
        }
        var state = cycle.Maneuvers;
        switch (def.SpellId)
        {
            case "retrace":
                if (state.RetraceLunation == Lunation(cycle))
                {
                    why = "Retrace has been cast this lunation already.";
                    return false;
                }
                if (!state.LastMarchValid || state.LastMarchLunation != Lunation(cycle)
                    || state.LastMarchToX != cycle.CastleX || state.LastMarchToY != cycle.CastleY)
                {
                    why = "There is no march of this lunation to undo.";
                    return false;
                }
                if (!string.IsNullOrEmpty(cycle.PendingCastleAssaultKingdomId))
                {
                    why = "There are soldiers in the camp. Answer them first.";
                    return false;
                }
                break;
            case "ley_tap":
                if (cycle.CastleRepairLunations > 0 || WorldClock.CastleBusy(cycle)
                    || !string.IsNullOrEmpty(cycle.PendingCastleAssaultKingdomId))
                {
                    why = "The castle cannot leap while its crews are at work or soldiers are in the camp.";
                    return false;
                }
                break;
            case "force_path":
                if (state.ForcePathArmed)
                {
                    why = "Force Path is already laid for the next march.";
                    return false;
                }
                break;
            case "verdant_passage":
                if (VerdantActive(cycle))
                {
                    why = "Verdant Passage already holds this lunation.";
                    return false;
                }
                break;
            case "veil":
                if (state.VeilArmed)
                {
                    why = "The next march is veiled already.";
                    return false;
                }
                break;
            case "campward":
                if (CampWarded(cycle) || state.CampwardArmed)
                {
                    why = "The camp is warded already.";
                    return false;
                }
                break;
            case "wayfarers_beacon":
                if (state.BeaconArmed)
                {
                    why = "The beacon is lit already.";
                    return false;
                }
                break;
        }
        if (def.Target != ManeuverTarget.None && Targets(cycle, def).Count == 0)
        {
            why = def.Target switch
            {
                ManeuverTarget.HeldProvince => "The guild holds no province within three hops.",
                ManeuverTarget.CastleFront => "No front contests the castle's province.",
                ManeuverTarget.ReachFront => "No front contests a province within the font's reach.",
                _ => "There is nothing within the font's reach to aim it at.",
            };
            return false;
        }
        return true;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Casting
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>Cast a maneuver at a target (a key from Targets, or "" for none). Returns true
    /// when it was cast, with the line for the player in <paramref name="line"/> (the refusal
    /// when it was not); <paramref name="moved"/> is true when the castle moved, so the map
    /// must open afresh.</summary>
    public static bool Cast(CycleState cycle, string spellId, string target, out string line, out bool moved)
    {
        moved = false;
        var def = Get(spellId);
        if (!CanCast(cycle, def, out line))
            return false;
        if (def.Target != ManeuverTarget.None)
        {
            bool aimed = false;
            foreach (var (key, _) in Targets(cycle, def))
            {
                if (key == target)
                {
                    aimed = true;
                    break;
                }
            }
            if (!aimed)
            {
                line = "That is not within the font's reach.";
                return false;
            }
        }
        var world = cycle.World;
        var state = cycle.Maneuvers;
        int cost = CostFor(cycle, def);
        string name = CastleHops.Chassis(cycle)?.Name ?? "The castle";
        KeeperHunt.HearCast(cycle, def.SpellId);   // provinces PV6: a cast is loud, heard where it is cast
        switch (def.SpellId)
        {
            case "force_path":
                state.ForcePathArmed = true;
                line = "Force Path is laid: the next march fords and climbs at no extra cost.";
                break;
            case "verdant_passage":
                state.VerdantLunation = Lunation(cycle);
                line = "Verdant Passage: until the new moon, woodland and wetland open to the castle as lowland.";
                break;
            case "veil":
                state.VeilArmed = true;
                line = "Veil: the next march leaves no trail.";
                break;
            case "campward":
                if (cycle.CastleExposed)
                {
                    state.WardedCampX = cycle.CastleX;
                    state.WardedCampY = cycle.CastleY;
                    line = "Campward: the wards rise around the camp. Nothing strikes it while the crews unpack.";
                }
                else
                {
                    state.CampwardArmed = true;
                    line = "Campward: the next camp will be warded.";
                }
                break;
            case "wayfarers_beacon":
                state.BeaconArmed = true;
                line = "Wayfarer's Beacon: the next march that ends in a province the guild holds makes no camp.";
                break;
            case "retrace":
            {
                int fromX = cycle.CastleX, fromY = cycle.CastleY;
                int refund = state.LastMarchFuel;
                int cap = cycle.CastleMaxFuel > 0 ? cycle.CastleMaxFuel : int.MaxValue;
                cycle.CastleFuel = Math.Min(cap, cycle.CastleFuel + refund);
                int backX = state.LastMarchFromX, backY = state.LastMarchFromY;
                bool veilKept = state.VeilArmed;   // the march unwinds; a Veil laid for the next one stays laid
                state.VeilArmed = true;            // and the unwinding leaves no trail for the Keeper
                ExpeditionAnchors.ParkCastleAt(cycle, backX, backY, name, 0);
                OnMoved(cycle, fromX, fromY, backX, backY, 0, false);
                state.VeilArmed = veilKept;
                state.LastMoveVeiled = true;
                state.WardedCampX = -1;
                state.WardedCampY = -1;
                state.RetraceLunation = Lunation(cycle);
                line = $"Retrace: the march unwinds. {name} stands where it began, {refund} fuel back in the furnace, "
                       + "and no camp to make.";
                moved = true;
                break;
            }
            case "ley_tap":
            {
                var to = world.Provinces[int.Parse(target)];
                var (x, y) = CastleHops.CampTile(world, to);
                int fromX = cycle.CastleX, fromY = cycle.CastleY;
                int days = CampDaysAfterMarch(cycle, x, y, CastleMarch.ArrivalCampDays);
                ExpeditionAnchors.ParkCastleAt(cycle, x, y, name, days);
                OnMoved(cycle, fromX, fromY, x, y, 0, false);
                line = $"Ley Leap: {name} folds along the ley and stands in {ProvinceControl.Name(to)}. "
                       + (days > 0 ? $"The crews need {days} day(s) to unpack." : "There is no camp to make.");
                moved = true;
                break;
            }
            case "deploy_waystation":
            {
                var to = world.Provinces[int.Parse(target)];
                RemoveWaystation(cycle);
                var (x, y) = CastleHops.CampTile(world, to);
                world.StagingPoints ??= new List<StagingPoint>();
                world.StagingPoints.Add(new StagingPoint
                {
                    X = x,
                    Y = y,
                    Name = $"Waystation, {ProvinceControl.Name(to)}",
                    Source = WaystationSource,
                    Available = true,
                });
                state.WaystationX = x;
                state.WaystationY = y;
                state.WaystationProvinceId = to.Id;
                line = $"Deploy Waystation: a forward camp stands in {ProvinceControl.Name(to)}. A party can stage "
                       + "there, and the font reaches one province beyond it, until the castle next moves.";
                moved = true;   // a new staging point: the map draws it afresh
                break;
            }
            case "clockwork_skimmer":
            {
                var to = world.Provinces[int.Parse(target)];
                int charted = ChartProvince(world, to);
                line = $"Clockwork Skimmer: the gadget comes home with {ProvinceControl.Name(to)} on paper, "
                       + $"{charted} tile(s) newly charted.";
                moved = charted > 0;   // the fog lifts: the map draws it afresh
                break;
            }
            case "castle_scry":
            {
                var to = world.Provinces[int.Parse(target)];
                line = "Scry Province. " + ScryProvince(cycle, to);
                moved = true;   // new marks: the map draws it afresh
                break;
            }
            case "castle_discord":
            {
                var to = world.Provinces[int.Parse(target)];
                string ruler = ProvinceControl.RulerOf(cycle, to);
                if (!ProvinceControl.IsKingdom(ruler) || cycle.Kingdoms == null
                    || !cycle.Kingdoms.TryGetValue(ruler, out var realm) || realm == null)
                {
                    line = "No realm rules that province. There is no plan there to break.";
                    return false;
                }
                if (realm.HinderedMoons > 0)
                {
                    line = $"{ProvinceControl.RealmName(cycle, ruler)} is in discord already.";
                    return false;
                }
                realm.HinderedMoons = 1;
                realm.Plan = new RivalPlan();
                line = $"Sow Discord: {ProvinceControl.RealmName(cycle, ruler)}'s plan falls apart. It holds no plan "
                       + "for the rest of this moon and all of the next.";
                break;
            }
            case "castle_bombard":
            {
                state.BombardedFronts ??= new Dictionary<string, int>();
                if (state.BombardedFronts.ContainsKey(target))
                {
                    line = "That front is under the castle's engines already.";
                    return false;
                }
                state.BombardedFronts[target] = 1;
                line = "Bombardment: the engines find their range. Your next Seize at that front counts double.";
                break;
            }
            case "thornwall":
            case "stasis_snare":
            {
                state.FrozenFronts ??= new Dictionary<string, int>();
                state.FrozenFronts[target] = 1;
                Warfront front = null;
                foreach (var wf in cycle.Warfronts)
                {
                    if (wf != null && wf.Id == target)
                    {
                        front = wf;
                        break;
                    }
                }
                var contested = ProvinceControl.ProvinceById(world, front?.ProvinceId ?? "");
                line = $"{def.Name}: the front at {ProvinceControl.Name(contested)} will stand still at the next new moon.";
                break;
            }
        }
        state.FontEssence = Math.Max(0, state.FontEssence - cost);
        GD.Print($"[CastleManeuvers] {def.Name} ({cost} Essence, {state.FontEssence} left): {line}");
        return true;
    }

    /// <summary>Chart every unseen tile of a province. Returns how many.</summary>
    private static int ChartProvince(WorldData world, Province province)
    {
        int charted = 0;
        for (int i = 0; i < world.Tiles.Length; i++)
        {
            if (world.ProvinceIndexAt(i % world.Width, i / world.Width) != province.Index)
                continue;
            if (world.Tiles[i].Discovery == TileDiscovery.Unseen)
            {
                world.Tiles[i].Discovery = TileDiscovery.Charted;
                charted++;
            }
        }
        return charted;
    }

    /// <summary>Strike the forward camp, if one stands.</summary>
    private static void RemoveWaystation(CycleState cycle)
    {
        var state = cycle?.Maneuvers;
        if (state == null || string.IsNullOrEmpty(state.WaystationProvinceId))
            return;
        cycle.World?.StagingPoints?.RemoveAll(sp => sp != null && sp.Source == WaystationSource);
        state.WaystationX = -1;
        state.WaystationY = -1;
        state.WaystationProvinceId = "";
    }

    // ══════════════════════════════════════════════════════════════════════
    // The march's hooks
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>The camp a march or a leap ending at (x, y) owes, in days: none when the beacon
    /// is lit and the guild holds the province there; and Campward, if armed, wards it. Uses
    /// up what it applies.</summary>
    public static int CampDaysAfterMarch(CycleState cycle, int x, int y, int normalDays)
    {
        var state = cycle?.Maneuvers;
        if (state == null)
            return normalDays;
        int days = normalDays;
        if (state.BeaconArmed && Holds(cycle, ProvinceBenefits.ProvinceAtTile(cycle, x, y)))
        {
            days = 0;
            state.BeaconArmed = false;
        }
        if (state.CampwardArmed && days > 0)
        {
            state.WardedCampX = x;
            state.WardedCampY = y;
            state.CampwardArmed = false;
        }
        return days;
    }

    /// <summary>The camp a march to (x, y) would owe, for the confirm: as CampDaysAfterMarch,
    /// using nothing up.</summary>
    public static int CampDaysPreview(CycleState cycle, int x, int y, int normalDays)
        => cycle?.Maneuvers != null && cycle.Maneuvers.BeaconArmed
           && Holds(cycle, ProvinceBenefits.ProvinceAtTile(cycle, x, y)) ? 0 : normalDays;

    /// <summary>What is armed for the next march, in words ("Force Path, Veil"), or "".</summary>
    public static string ArmedText(CycleState cycle)
    {
        var state = cycle?.Maneuvers;
        if (state == null)
            return "";
        var armed = new List<string>();
        if (state.ForcePathArmed)
            armed.Add("Force Path");
        if (VerdantActive(cycle))
            armed.Add("Verdant Passage");
        if (state.VeilArmed)
            armed.Add("Veil");
        if (state.CampwardArmed)
            armed.Add("Campward");
        if (state.BeaconArmed)
            armed.Add("Wayfarer's Beacon");
        return string.Join(", ", armed);
    }

    /// <summary>The castle moved: a march (which Retrace can undo, and which uses up Force
    /// Path), a leap or a retrace. The Veil, if armed, hides this move; the forward camp is
    /// struck.</summary>
    public static void OnMoved(CycleState cycle, int fromX, int fromY, int toX, int toY, int fuel, bool march)
    {
        var state = cycle?.Maneuvers;
        if (state == null)
            return;
        state.LastMoveVeiled = state.VeilArmed;
        state.VeilArmed = false;
        KeeperHunt.Learn(cycle, state.LastMoveVeiled);   // provinces PV6: the Keeper reads the trail
        if (march)
        {
            state.ForcePathArmed = false;
            state.LastMarchValid = true;
            state.LastMarchFromX = fromX;
            state.LastMarchFromY = fromY;
            state.LastMarchToX = toX;
            state.LastMarchToY = toY;
            state.LastMarchFuel = fuel;
            state.LastMarchLunation = Lunation(cycle);
        }
        else
        {
            state.LastMarchValid = false;
        }
        RemoveWaystation(cycle);
        // Provinces PV5 (PR80, PR81): the castle surveys where it has come to stand.
        string survey = CastleReach.Survey(cycle);
        if (survey.Length > 0)
        {
            cycle.PendingSiegeReports ??= new List<string>();
            cycle.PendingSiegeReports.Add(survey);
        }
    }

    /// <summary>A front a spell holds still: uses up one new moon of the hold and says so. True
    /// when the front stands still this lunation.</summary>
    public static bool TakeFreeze(CycleState cycle, Warfront wf)
    {
        var frozen = cycle?.Maneuvers?.FrozenFronts;
        if (frozen == null || wf == null || string.IsNullOrEmpty(wf.Id)
            || !frozen.TryGetValue(wf.Id, out int left) || left <= 0)
        {
            return false;
        }
        if (left <= 1)
            frozen.Remove(wf.Id);
        else
            frozen[wf.Id] = left - 1;
        var contested = ProvinceControl.ProvinceById(cycle.World, wf.ProvinceId);
        string where = contested != null ? ProvinceControl.Name(contested) : wf.DefenderName;
        cycle.PendingSiegeReports ??= new List<string>();
        cycle.PendingSiegeReports.Add($"The front at {where} stands still this lunation: the guild's spell holds it.");
        return true;
    }

    /// <summary>The new moon: the font fills, a ward whose camp has ended lapses, and holds on
    /// fronts that have closed are dropped. Returns lines of news (none today).</summary>
    public static List<string> NewMoon(CycleState cycle)
    {
        var lines = new List<string>();
        var state = cycle?.Maneuvers;
        if (state == null)
            return lines;
        state.FontEssence = FontMax;
        if (state.WardedCampX >= 0
            && (cycle.CastleRepairLunations <= 0 || state.WardedCampX != cycle.CastleX || state.WardedCampY != cycle.CastleY))
        {
            state.WardedCampX = -1;
            state.WardedCampY = -1;
        }
        if (state.FrozenFronts != null && state.FrozenFronts.Count > 0)
        {
            var open = new HashSet<string>();
            foreach (var wf in cycle.Warfronts ?? new List<Warfront>())
            {
                if (wf != null && !wf.Closed && !string.IsNullOrEmpty(wf.Id))
                    open.Add(wf.Id);
            }
            var gone = new List<string>();
            foreach (var key in state.FrozenFronts.Keys)
            {
                if (!open.Contains(key))
                    gone.Add(key);
            }
            foreach (var key in gone)
                state.FrozenFronts.Remove(key);
        }
        // Provinces PV5 (PR80, PR81): at each new moon the castle's lens surveys its province
        // and the ones beside it.
        string survey = CastleReach.Survey(cycle);
        if (survey.Length > 0)
            lines.Add(survey);
        return lines;
    }

    /// <summary>[DEBUG] The guild learns every maneuver's spell and the font fills, so a gate
    /// can try each one. Returns a line for the log.</summary>
    public static string DebugLearnAll(CycleState cycle)
    {
        if (cycle?.Grimoire == null || cycle.Maneuvers == null)
            return null;
        cycle.Grimoire.KnownSpellIds ??= new List<string>();
        int learned = 0;
        foreach (var def in All)
        {
            if (IsCastleWork(def.SpellId))
                continue;   // C1: not a spell, nothing to learn
            if (!cycle.Grimoire.KnownSpellIds.Contains(def.SpellId))
            {
                cycle.Grimoire.KnownSpellIds.Add(def.SpellId);
                learned++;
            }
        }
        cycle.Maneuvers.FontEssence = FontMax;
        return $"The guild knows every maneuver ({learned} newly learned); the font holds {FontMax} Essence.";
    }
}
