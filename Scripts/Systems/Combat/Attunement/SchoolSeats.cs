using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// SchoolSeats.cs
//
// Purpose:        The combat side of the school seats (campus_building_
//                 upgrades_design_v1 §8d). One entry point at combat
//                 start, one at victory, so CombatManager carries two
//                 calls rather than a seat's worth of rules. Seats land
//                 here one school at a time; the first is the Crucible
//                 of Storms (Elementalist, ruled 2026-09-29).
//
//                 Crucible of Storms:
//                   T1 Fronts           every Elementalist opens a fight
//                                       with 1 attunement in the timeline's
//                                       chosen element (CycleState.CrucibleFront)
//                   T2 Standing Weather attunement carries between fights of
//                                       one expedition at half, rounded down
//                   T3 Monoelement      bursts at 3; only one element holds
//                      or Confluence    the third different element cast in a
//                                       turn bursts for free
// Layer:          System (combat)
// Collaborators:  ElementalAttunement.cs (BurstAt, Monoelement, Confluence,
//                 GainCharge), Charters.cs (doctrine gate), CombatManager
//                 (the two calls), ExpeditionManager (clears the carry at
//                 a fresh expedition and when the sortie ends),
//                 CampusSeatPanel (sets the front).
// Notes:          The weather carry is runtime state, like the rest of an
//                 expedition's combat round-trip: quitting mid-expedition
//                 drops it, which costs one fight's head start at most.
// ============================================================

/// <summary>School seats in combat. Stateless apart from the weather carry.</summary>
public static class SchoolSeats
{
    public const string CrucibleId = "crucible_of_storms";
    public const string Monoelement = "monoelement";
    public const string Confluence = "confluence";

    /// <summary>Loadout key ("wizard" or companion id) → element → charges the
    /// next fight of this expedition opens with (Standing Weather).</summary>
    private static readonly Dictionary<string, Dictionary<ElementTag, int>> _weatherCarry = new();

    // ── Reads ────────────────────────────────────────────────────────────

    /// <summary>A seat's tier, 0 when unbuilt or unsited.</summary>
    public static int Tier(GuildSaveData save, string buildingId)
    {
        if (save?.Buildings == null)
        {
            return 0;
        }
        foreach (var b in save.Buildings)
        {
            if (b.Id == buildingId)
            {
                return b.IsFunctional ? b.Tier : 0;
            }
        }
        return 0;
    }

    /// <summary>The timeline's front as an element, or null for none.</summary>
    public static ElementTag? Front(GuildSaveData save)
    {
        string f = save?.Cycle?.CrucibleFront;
        if (string.IsNullOrEmpty(f))
        {
            return null;
        }
        return Enum.TryParse<ElementTag>(f, true, out var e) ? e : (ElementTag?)null;
    }

    /// <summary>A blighted Crucible (campus corruption, §11c) no longer takes
    /// orders: the storm picks its own front each moon, but hits twice as hard.</summary>
    public const int BlightedFrontCharges = 2;

    private static readonly ElementTag[] FrontElements =
        { ElementTag.Fire, ElementTag.Ice, ElementTag.Storm, ElementTag.Earth };

    public static bool IsWildStorm(GuildSaveData save) => CampusBlight.IsBlighted(save, CrucibleId);

    /// <summary>This moon's wild front: seeded by the world and the moon, so it
    /// holds for the whole moon and a reload cannot reroll it.</summary>
    public static ElementTag WildFront(GuildSaveData save)
    {
        int lunation = save?.Cycle?.Calendar?.CurrentLunation ?? 1;
        int seed = unchecked((save?.Cycle?.WorldSeed ?? 0) * 397 ^ lunation * 7919);
        return FrontElements[(int)((uint)seed % (uint)FrontElements.Length)];
    }

    /// <summary>Set (or clear, with null) the timeline's front.</summary>
    public static void SetFront(GuildSaveData save, ElementTag? front)
    {
        if (save?.Cycle == null)
        {
            return;
        }
        save.Cycle.CrucibleFront = front.HasValue ? front.Value.ToString() : "";
        SaveManager.MarkDirty();
    }

    // ── Combat hooks ─────────────────────────────────────────────────────

    /// <summary>Combat start, after every unit's attunement is initialised.
    /// <paramref name="keyFor"/> maps a unit to its loadout key.</summary>
    public static void ApplyCombatStart(IEnumerable<Unit> units, Func<Unit, string> keyFor)
    {
        var save = SaveManager.ActiveSave;
        int crucible = Tier(save, CrucibleId);
        if (units == null || crucible <= 0)
        {
            return;
        }
        bool mono = Charters.IsActive(save, CrucibleId, Monoelement);
        bool confluence = Charters.IsActive(save, CrucibleId, Confluence);
        var front = Front(save);
        int frontCharges = 1;
        if (IsWildStorm(save))
        {
            front = WildFront(save);
            frontCharges = BlightedFrontCharges;
        }

        foreach (var u in units)
        {
            if (u?.Attunement is not ElementalAttunement att)
            {
                continue;
            }
            att.Monoelement = mono;
            att.BurstAt = mono ? 3 : ElementalAttunement.BurstThreshold;
            att.Confluence = confluence;

            if (front.HasValue)
            {
                att.GainCharge(front.Value, frontCharges);
            }

            if (crucible >= 2 && _weatherCarry.TryGetValue(keyFor(u), out var carry))
            {
                foreach (var kv in carry)
                {
                    if (kv.Value > 0)
                    {
                        att.GainCharge(kv.Key, kv.Value);
                    }
                }
            }
            GD.Print($"[Crucible] {u.Name}: front {(front.HasValue ? front.Value.ToString() : "none")}, "
                     + $"mono {mono}, confluence {confluence}.");
        }
        // The carry is spent by the fight that opens with it; only a win
        // (RecordVictory) writes the next one. A loss or a retreat carries nothing.
        _weatherCarry.Clear();
    }

    /// <summary>Victory: record what each Elementalist's weather carries into the
    /// next fight of this expedition (Standing Weather, T2).</summary>
    public static void RecordVictory(IEnumerable<Unit> units, Func<Unit, string> keyFor)
    {
        var save = SaveManager.ActiveSave;
        _weatherCarry.Clear();
        if (units == null || Tier(save, CrucibleId) < 2)
        {
            return;
        }
        foreach (var u in units)
        {
            if (u?.Attunement is not ElementalAttunement att || !u.Stats.IsAlive)
            {
                continue;
            }
            var carry = new Dictionary<ElementTag, int>();
            foreach (var kv in att.Charges)
            {
                if (kv.Value / 2 > 0)
                {
                    carry[kv.Key] = kv.Value / 2;
                }
            }
            if (carry.Count > 0)
            {
                _weatherCarry[keyFor(u)] = carry;
            }
        }
    }

    /// <summary>The expedition's weather ends: a fresh start, or the sortie over.</summary>
    public static void ClearWeatherCarry() => _weatherCarry.Clear();
}
