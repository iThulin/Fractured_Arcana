using Godot;
using System.Collections.Generic;
// ============================================================
// ProvinceBenefits.cs: what holding a province gives (2026-11-05)
//
// Purpose:        Slice PV2c of the provinces spec, section 7i
//                 (docs/provinces_v1_7i_held_benefits.md), built on its
//                 defaults (PR45 to PR50, ruled 2026-11-05). One question
//                 decides every benefit, HeldForBenefits: the province is
//                 held, or it is the estate of a courtier who is the guild's
//                 Patron (PR17). The Patron's estate counts for the benefits
//                 and nothing else: it is not Held, so it gives no hold news,
//                 no held neighbour's +1, no stability and no gold edge.
//
//                 The benefits:
//                   PR45  a party banks its hold and refills its wands
//                         anywhere in such a province (FieldMarch.BankHere),
//                         and the castle's camp there takes an ally's
//                         land's odds on its threat roll (CastleThreats)
//                   PR46  a spell cast from it costs 1 Essence less, never
//                         below 1 (OverworldSpellManager.HeldGroundDiscount)
//                   PR47  at each new moon its unseen places are Sighted,
//                         under camp sight's rules (no staging, no Concord
//                         node, nothing under Classic Exploration)
//                   PR48  at each new moon it sends 2 supplies; the
//                         Economic favor spent as its deed doubles its
//                         estate's next yield (FavorDeeds)
//                   PR50  water opened into marsh joins the nearest
//                         province (ExpeditionManager.SpellForcePath)
// Layer:          System (strategic; static, no nodes)
// Collaborators:  Province (Held, YieldDoubledLunation), CouncilState
//                 (CourtState.IsPatron, CourtierState.EstateProvinceId),
//                 StrategicView.RunLunationTick (NewMoon), LeadService
//                 (Prune, as camp sight does), ProvinceInfluence
//                 (NearestProvince), ProvinceBuilder (AdoptTile),
//                 StrategicView.ProvinceCard.cs and ProvinceHover.cs
//                 (what the map says), ProvinceSaveAssert (AssertBenefits)
// See:            docs/provinces_v1_7i_held_benefits.md
// ============================================================
/// <summary>What holding a province gives the guild (PV2c). Stateless: the one stored
/// value is each province's YieldDoubledLunation. Writes nothing to disk itself: every
/// caller already marks the save dirty after it (the lunation tick, a favor spent, a spell
/// cast).</summary>
public static class ProvinceBenefits
{
    /// <summary>Supplies each province held for its benefits sends at the new moon (PR48).</summary>
    public const int HeldYield = 2;

    /// <summary>Essence off a spell cast from a province held for its benefits, never taking a
    /// cost below 1 (PR46).</summary>
    public const int HeldCastDiscount = 1;

    /// <summary>Percent added to the chance the castle's camp is struck when it stands in a
    /// province held for its benefits: an ally's land's modifier, on top of the realm's own,
    /// floored at 0 (PR45).</summary>
    public const int HeldCampModifier = -25;

    // ══════════════════════════════════════════════════════════════════════
    // Reading
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>True when the province gives the guild its benefits: it is held, or it is
    /// the estate of a courtier who is the guild's Patron (PR17).</summary>
    public static bool HeldForBenefits(CycleState cycle, Province province)
        => province != null && (province.Held || EstatePatron(cycle, province) != null);

    /// <summary>The courtier whose estate the province is and who serves as the guild's
    /// Patron at their court, as the council screen reads it (CourtState.IsPatron); the
    /// first in court order. Null when there is none.</summary>
    public static CourtierState EstatePatron(CycleState cycle, Province province)
    {
        if (province == null || string.IsNullOrEmpty(province.Id) || string.IsNullOrEmpty(province.KingdomId)
            || cycle?.Council?.Courts == null
            || !cycle.Council.Courts.TryGetValue(province.KingdomId, out var court) || court?.Courtiers == null)
        {
            return null;
        }
        foreach (var courtier in court.Courtiers)
        {
            if (courtier != null && courtier.EstateProvinceId == province.Id && court.IsPatron(courtier.Id))
            {
                return courtier;
            }
        }
        return null;
    }

    /// <summary>The province holding a tile, or null for water, land in no province, or a
    /// world without provinces.</summary>
    public static Province ProvinceAtTile(CycleState cycle, int x, int y)
    {
        var world = cycle?.World;
        return world != null && world.HasProvinces() ? world.ProvinceAt(x, y) : null;
    }

    /// <summary>True when the tile lies in a province held for its benefits.</summary>
    public static bool HeldForBenefitsAt(CycleState cycle, int x, int y)
        => HeldForBenefits(cycle, ProvinceAtTile(cycle, x, y));

    /// <summary>The name of the province holding a tile, for a line of news, or "the
    /// province" when it has none.</summary>
    public static string NameAt(CycleState cycle, int x, int y)
    {
        var province = ProvinceAtTile(cycle, x, y);
        if (province == null)
        {
            return "the province";
        }
        return string.IsNullOrEmpty(province.Name) ? province.Id : province.Name;
    }

    /// <summary>The readout's and the card's note for a Patron's estate the guild does not
    /// hold: " Held for its benefits: the Patron's estate." Otherwise "".</summary>
    public static string PatronNote(CycleState cycle, Province province)
        => province != null && !province.Held && EstatePatron(cycle, province) != null
            ? " Held for its benefits: the Patron's estate."
            : "";

    // ══════════════════════════════════════════════════════════════════════
    // The new moon (PR47, PR48)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>What holding gives at the new moon: each province held for its benefits has
    /// its unseen places Sighted, then sends its supplies. Returns the frontier news.
    /// StrategicView.RunLunationTick calls it once a lunation, after the influence tick has
    /// moved the holds.</summary>
    public static List<string> NewMoon(CycleState cycle)
    {
        var lines = new List<string>();
        if (cycle?.World == null || !cycle.World.HasProvinces())
        {
            return lines;
        }
        lines.AddRange(SightHeldPlaces(cycle));
        string yield = PayYields(cycle);
        if (yield.Length > 0)
        {
            lines.Add(yield);
        }
        return lines;
    }

    /// <summary>PR47: every place not yet on the map in a province held for its benefits
    /// becomes Sighted, through camp sight's own write: WorldPoi.Discovered, its tile
    /// charted and never downgraded. Sighting grants no staging, a Veiled Concord node is
    /// never sighted, and nothing is sighted under the Classic Exploration debug switch.
    /// One line of news per province, in province order.</summary>
    private static List<string> SightHeldPlaces(CycleState cycle)
    {
        var lines = new List<string>();
        var world = cycle.World;
        if (world.Pois == null || PlayerSession.DebugClassicExploration)
        {
            return lines;
        }
        var heldFor = new Dictionary<int, bool>();
        var counts = new SortedDictionary<int, int>();
        foreach (var poi in world.Pois)
        {
            if (poi == null || poi.Discovered || poi.Kind == PoiKind.Concord || !world.InBounds(poi.X, poi.Y))
            {
                continue;
            }
            int index = world.ProvinceIndexAt(poi.X, poi.Y);
            if (index < 0)
            {
                continue;
            }
            if (!heldFor.TryGetValue(index, out bool held))
            {
                held = HeldForBenefits(cycle, world.Provinces[index]);
                heldFor[index] = held;
            }
            if (!held)
            {
                continue;
            }
            poi.Discovered = true;
            if (world.TryIndex(poi.X, poi.Y, out int tile) && world.Tiles[tile].Discovery == TileDiscovery.Unseen)
            {
                world.Tiles[tile].Discovery = TileDiscovery.Charted;
            }
            counts[index] = counts.TryGetValue(index, out int n) ? n + 1 : 1;
        }
        if (counts.Count == 0)
        {
            return lines;
        }
        LeadService.Prune(cycle);   // a lead on a place now Sighted resolves, as from camp
        foreach (var kv in counts)
        {
            string label = Label(cycle, world.Provinces[kv.Key]);
            lines.Add(kv.Value == 1
                ? $"Word comes in from {label}: one new place there is marked on the map."
                : $"Word comes in from {label}: {kv.Value} new places there are marked on the map.");
            GD.Print($"[ProvinceBenefits] {label}: {kv.Value} place(s) Sighted at the new moon.");
        }
        return lines;
    }

    /// <summary>PR48: each province held for its benefits sends HeldYield supplies, twice that
    /// when the Economic favor marked it for this new moon. A mark is spent at the first new
    /// moon it reaches, held then or not. One line of news with the total.</summary>
    private static string PayYields(CycleState cycle)
    {
        int lunation = cycle.Calendar?.CurrentLunation ?? 0;
        int total = 0;
        int paying = 0;
        foreach (var province in cycle.World.Provinces)
        {
            bool doubled = province.YieldDoubledLunation >= 0 && lunation >= province.YieldDoubledLunation;
            if (doubled)
            {
                province.YieldDoubledLunation = -1;
            }
            if (!HeldForBenefits(cycle, province))
            {
                continue;
            }
            total += doubled ? 2 * HeldYield : HeldYield;
            paying++;
        }
        if (total == 0)
        {
            return "";
        }
        cycle.Supplies += total;
        GD.Print($"[ProvinceBenefits] {paying} province(s) held for their benefits send {total} supplies.");
        return paying == 1
            ? $"The held province sends {total} supplies."
            : $"The {paying} held provinces send {total} supplies.";
    }

    /// <summary>The Economic favor spent as its deed (PR48, PR44's deferred half): its
    /// estate's yield is doubled at the next new moon, if the estate is held for its
    /// benefits then.</summary>
    public static void DoubleNextYield(CycleState cycle, int index)
    {
        var world = cycle?.World;
        if (world == null || !world.HasProvinces() || index < 0 || index >= world.Provinces.Count)
        {
            return;
        }
        world.Provinces[index].YieldDoubledLunation = (cycle.Calendar?.CurrentLunation ?? 0) + 1;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Land opened in play (PR50)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>Water just opened into land at (x,y) joins the nearest province, found by
    /// the walk a deed off the provinces uses (ProvinceInfluence.NearestProvince). The tile
    /// keeps its empty KingdomId: opened land is no kingdom's (PV1). Returns the province's
    /// index, or -1 when the tile is water, already in a province, or has none in reach.</summary>
    public static int JoinOpenedLand(WorldData world, int x, int y)
    {
        if (world == null || !world.HasProvinces() || !world.InBounds(x, y) || world.ProvinceIndexAt(x, y) >= 0)
        {
            return -1;
        }
        int nearest = ProvinceInfluence.NearestProvince(world, x, y);
        if (nearest < 0 || !ProvinceBuilder.AdoptTile(world, x, y, nearest))
        {
            return -1;
        }
        GD.Print($"[ProvinceBenefits] Opened land at ({x},{y}) joins {world.Provinces[nearest].Id}.");
        return nearest;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Words
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>"North Weald, Veyra": the province's name, then its realm's, as the hold
    /// news names it (ProvinceInfluence).</summary>
    private static string Label(CycleState cycle, Province province)
    {
        string name = string.IsNullOrEmpty(province.Name) ? province.Id : province.Name;
        if (string.IsNullOrEmpty(province.KingdomId) || cycle?.Kingdoms == null
            || !cycle.Kingdoms.TryGetValue(province.KingdomId, out var kingdom) || kingdom == null)
        {
            return name;
        }
        string realm = string.IsNullOrEmpty(kingdom.DisplayName) ? province.KingdomId : kingdom.DisplayName;
        return $"{name}, {realm}";
    }
}
