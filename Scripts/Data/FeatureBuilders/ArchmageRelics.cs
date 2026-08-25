using Godot;
using System.Linq;

// ============================================================
// ArchmageRelics.cs
//
// Purpose:        Q4.2. Each archmage's ONE authored Legendary
//                 relic (companion_item_systems v2.1 §7c):
//                 Overthrow drops it at the resolution victory;
//                 Unite gifts it when the moon the alliance was
//                 sworn under returns (the anniversary, with patience
//                 priced in); Corrupted archmagi's relics are
//                 BLOCKED on the Auction House building (unbuilt)
//                 and stay ungrantable until it exists.
// Layer:          Data (FeatureBuilders)
// Collaborators:  Data/Items/relic_<archmageId>.json (the eight),
//                 ExpeditionManager (overthrow return, Step 9),
//                 RecruitmentSources.OnArchmageUnited (records the
//                 unite lunation), StrategicView.RunLunationTick
//                 (anniversary check), ItemDatabase / ArmoryData.
// Notes:          RULING (2026-08-13): "first anniversary lunation"
//                 read against the live 12-lunation cycle, since a
//                 12-lunation year would put every anniversary
//                 past the Conjunction. The calendar's moon names
//                 cycle every 8 lunations (CalendarState.MoonIndex),
//                 so the anniversary = the unite MOON's return,
//                 8 lunations later. Reachable iff you unite by
//                 lunation 4. Patience priced in, payoff possible.
// ============================================================

/// <summary>Grant routing for the eight archmage relics. All grants are
/// idempotent per relic (Legendary unique-owned, v1 locked). The Armory is
/// the single source of owned-ness, no flags.</summary>
public static class ArchmageRelics
{
    /// <summary>Lunations until a united archmage's relic is gifted, which is the
    /// unite moon's return (see the header ruling).</summary>
    public const int AnniversaryLunations = 8;

    public static string RelicIdFor(string archmageId) =>
        string.IsNullOrEmpty(archmageId) ? null : $"relic_{archmageId}";

    /// <summary>Grant an archmage's relic if it exists and isn't owned.
    /// Returns the toast/report line, or null on no-op (unknown relic id or
    /// already owned, both silent by design).</summary>
    public static string TryGrant(string archmageId, string how)
    {
        var save = SaveManager.ActiveSave;
        string relicId = RelicIdFor(archmageId);
        if (save == null || relicId == null) return null;

        if (save.Armory.OwnedItems.Any(i => i.DefinitionId == relicId))
            return null; // unique-owned: the Armory is the truth

        var def = ItemDatabase.Get(relicId);
        if (def == null)
        {
            GD.PrintErr($"[Relic] No relic JSON for '{archmageId}' (expected {relicId}).");
            return null;
        }

        save.Armory.AddItem(def);
        SaveManager.MarkDirty();
        GD.Print($"[Relic] {def.Name} granted ({how}).");
        return $"{def.Name} passes to the guild, {how}.";
    }

    /// <summary>Lunation-tick check: any Allied archmage whose unite moon has
    /// returned (≥ 8 lunations, idempotent via unique-owned) yields the gift.
    /// Call once per lunation from RunLunationTick.</summary>
    public static void TickUniteAnniversaries(CycleState cycle)
    {
        var campaign = cycle?.Campaign;
        if (campaign == null || campaign.UniteLunations.Count == 0) return;

        int now = cycle.Calendar.CurrentLunation;
        foreach (var kv in campaign.UniteLunations)
        {
            if (now - kv.Value < AnniversaryLunations) continue;
            if (campaign.GetDisposition(kv.Key) != ArchmageDisposition.Allied)
                continue; // the alliance must still stand when the moon returns

            string line = TryGrant(kv.Key, "the alliance's first anniversary gift");
            if (line != null)
                GD.Print($"[Relic] Anniversary: {line}");
        }
    }
}
