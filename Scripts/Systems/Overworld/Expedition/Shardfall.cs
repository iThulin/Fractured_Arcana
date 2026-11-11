using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// Shardfall.cs
//
// Purpose:        The shards as places (docs/shardfall_v1.md, stage H1).
//                 One shard a timeline: when the guild takes one, the
//                 other vaults seal until the next timeline (S1). A
//                 vault whose shard is already seated in the campus is
//                 empty. A vault that is neither is live, and it seeps
//                 into the province around its gate: every site there
//                 is stronger and pays more, and its creatures hunt the
//                 party whatever the local court thinks (S2).
//
//                 This class only answers questions. The answers are
//                 read by ExpeditionManager (the gate, the seep in the
//                 field), KingdomTickSimulation.Plans (what a realm may
//                 seek) and StrategicView.ProvinceCard (the card's line).
// Layer:          System
// Collaborators:  ShardZone (WorldData.cs), GuildSaveData (the ledger's
//                 fragment_<key>_collected flags), Province.cs
// See:            docs/shardfall_v1.md sections 2 and 5
// ============================================================

/// <summary>The state of the shard vaults in a timeline, and the seep around a live one.</summary>
public static class Shardfall
{
    /// <summary>Enemy strength in a seeped province is multiplied by this.</summary>
    public const float SeepFightMult = 1.3f;

    /// <summary>Gold from fights and stashes in a seeped province is multiplied by this.</summary>
    public const float SeepPayMult = 1.5f;

    // ── H2 (S3): the excavation ──────────────────────────────────────────

    /// <summary>What the party digs by winning a fight in the seep.</summary>
    public const int DigPerFight = 12;
    /// <summary>By beating a guard there.</summary>
    public const int DigPerGuard = 18;
    /// <summary>By working any other site there.</summary>
    public const int DigPerSite = 6;

    // H4 (S5): a garrison left standing this many new moons claims the shard for the timeline.
    public const int ClaimMoons = 3;
    /// <summary>At most this many vaults are held or claimed by realms at once (Claude's call,
    /// 2026-11-11): in play all six were garrisoned in the same moon.</summary>
    public const int MaxRivalVaults = 2;
    /// <summary>H4 (S6): the share of the excavation that carries to the next timeline.</summary>
    public const float CarryShare = 0.5f;

    // H5: groves (ruled 2026-11-11: any school may raise one, five moons, 4 supplies a moon,
    // and a calm seep keeps its strength and its pay).
    public const int GroveMoons = 5;
    public const int GroveSupplies = 4;
    public const int GrovesForDruid = 3;
    public const string DruidGrovesFlag = "druid_groves_complete";

    /// <summary>A grove stands in this vault's seep, awake or not.</summary>
    public static bool HasGrove(GuildSaveData save, ShardZone zone)
        => save?.Ledger?.GroveKeys != null && zone != null && save.Ledger.GroveKeys.Contains(zone.FragmentKey);

    /// <summary>A grove stands here and the guild has not dug under it this timeline. An awake
    /// grove calms the seep (nothing there hunts the party) and wards the shard from realms.</summary>
    public static bool GroveAwake(GuildSaveData save, ShardZone zone)
        => HasGrove(save, zone) && !zone.GroveAsleep;

    public static int GroveCount(GuildSaveData save) => save?.Ledger?.GroveKeys?.Count ?? 0;

    /// <summary>Record a grove. Returns the line for the player.</summary>
    public static string RaiseGrove(GuildSaveData save, ShardZone zone)
    {
        if (save?.Ledger == null || zone == null)
        {
            return "";
        }
        save.Ledger.GroveKeys ??= new List<string>();
        if (!save.Ledger.GroveKeys.Contains(zone.FragmentKey))
        {
            save.Ledger.GroveKeys.Add(zone.FragmentKey);
        }
        int groves = save.Ledger.GroveKeys.Count;
        string line = $"A grove stands at {zone.Name}, in this timeline and every one after. Nothing in its seep hunts you, "
                      + "and no realm can garrison its vault. Digging here puts the grove to sleep until the next timeline. "
                      + $"Groves raised: {groves} of {GrovesForDruid}.";
        if (groves >= GrovesForDruid && !save.Ledger.MetaNarrativeFlags.Contains(DruidGrovesFlag))
        {
            save.Ledger.MetaNarrativeFlags.Add(DruidGrovesFlag);
            line += " Three seeps are in harmony. The top of the Druid's class is open.";
        }
        GD.Print($"[Shardfall] {line}");
        return line;
    }

    // H3 (S4): the descent's two fights before the guardian, as multiples of the ground's
    // own strength (which already carries the seep).
    public const float OuterWardsMult = 1.15f;
    public const float InnerGuardMult = 1.35f;
    public const int DescentStages = 2;

    public static string DescentStageName(int stage) => stage <= 0 ? "outer wards" : "inner guard";
    /// <summary>A patrol beaten in the seep (H2c): the seep's hunters guard the dig too.</summary>
    public const int DigPerPatrol = 8;
    /// <summary>What a party posted on the vault digs in a moon.</summary>
    public const int DigPerPostedMoon = 15;
    /// <summary>Every gain is multiplied by this while the castle stands in reach of the vault.</summary>
    public const float CastleDigMult = 1.5f;

    /// <summary>Is the vault dug out? Its gate can be entered.</summary>
    public static bool DugOut(ShardZone zone) => zone != null && zone.Excavation >= 100;

    /// <summary>Dig toward a vault. Returns what was gained, after the castle's help and the cap.</summary>
    public static int Dig(CycleState cycle, ShardZone zone, int amount)
    {
        if (zone == null || amount <= 0 || zone.Excavation >= 100)
        {
            return 0;
        }
        if (cycle != null && CastleReach.InReach(cycle, zone.GateX, zone.GateY))
        {
            amount = (int)Math.Round(amount * CastleDigMult);
        }
        int before = zone.Excavation;
        zone.Excavation = Math.Min(100, before + amount);
        // H5 (SH12): digging under a grove puts it to sleep for this timeline.
        if (zone.Excavation > before && GroveAwake(SaveManager.ActiveSave, zone))
        {
            zone.GroveAsleep = true;
            string asleep = $"Your digging has put the grove at {zone.Name} to sleep. Its seep hunts you again, "
                            + "and realms may garrison the vault, until the next timeline.";
            if (cycle != null)
            {
                cycle.PendingSiegeReports ??= new List<string>();
                cycle.PendingSiegeReports.Add(asleep);
            }
            GD.Print($"[Shardfall] {asleep}");
        }
        return zone.Excavation - before;
    }

    /// <summary>The line for a gain, or "" for none.</summary>
    public static string DigLine(ShardZone zone, int gained)
    {
        if (zone == null || gained <= 0)
        {
            return "";
        }
        return zone.Excavation >= 100
            ? $"The vault at {zone.Name} is dug out. You can enter its gate."
            : $"Excavation at {zone.Name}: {zone.Excavation} of 100 (+{gained}).";
    }

    /// <summary>Has the guild taken its shard in this timeline? A world is one timeline's, so a
    /// vault whose shard was picked up in it says so.</summary>
    public static bool TakenThisTimeline(CycleState cycle)
    {
        if (cycle?.World?.ShardZones == null)
        {
            return false;
        }
        foreach (var zone in cycle.World.ShardZones)
        {
            if (zone != null && zone.ShardCollected)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The vault's shard was taken in an earlier timeline: the vault stands empty.</summary>
    public static bool Empty(GuildSaveData save, ShardZone zone)
        => save != null && zone != null && !zone.ShardCollected
           && save.HasFlag($"fragment_{zone.FragmentKey}_collected");

    /// <summary>The vault holds its shard, but the guild has taken another this timeline.</summary>
    public static bool Sealed(GuildSaveData save, ShardZone zone)
        => save != null && zone != null && !zone.ShardCollected && !Empty(save, zone)
           && (TakenThisTimeline(save.Cycle) || Claimed(zone));

    /// <summary>H4 (S5): a realm's garrison stood long enough and took the shard for this
    /// timeline. The shard is in its vault again in the next.</summary>
    public static bool Claimed(ShardZone zone)
        => zone != null && !string.IsNullOrEmpty(zone.ClaimedByKingdomId);

    /// <summary>How many vaults realms hold or have claimed.</summary>
    public static int RivalVaults(WorldData world)
    {
        int n = 0;
        if (world?.ShardZones != null)
        {
            foreach (var zone in world.ShardZones)
            {
                if (zone != null && (Claimed(zone) || !string.IsNullOrEmpty(zone.HeldByKingdomId)))
                {
                    n++;
                }
            }
        }
        return n;
    }

    /// <summary>H4 (S6): a timeline that ends without a shard keeps part of its best
    /// excavation, on that one vault. Called as the timeline ends, before its world is gone.</summary>
    public static void StoreCarry(GuildSaveData save, CycleState ending)
    {
        if (save?.Ledger == null)
        {
            return;
        }
        save.Ledger.CarriedDigKey = "";
        save.Ledger.CarriedDig = 0;
        if (ending?.World?.ShardZones == null || TakenThisTimeline(ending))
        {
            return;
        }
        ShardZone best = null;
        foreach (var zone in ending.World.ShardZones)
        {
            if (zone != null && zone.Excavation > 0 && !Empty(save, zone)
                && (best == null || zone.Excavation > best.Excavation))
            {
                best = zone;
            }
        }
        if (best == null)
        {
            return;
        }
        save.Ledger.CarriedDigKey = best.FragmentKey;
        save.Ledger.CarriedDig = (int)Math.Round(best.Excavation * CarryShare);
        GD.Print($"[Shardfall] {save.Ledger.CarriedDig} of the excavation at {best.Name} carries to the next timeline.");
    }

    /// <summary>H4 (S6): give the new timeline's vault its head start. Called once the new
    /// world exists.</summary>
    public static void ApplyCarry(GuildSaveData save, WorldData world)
    {
        if (save?.Ledger == null || world?.ShardZones == null
            || string.IsNullOrEmpty(save.Ledger.CarriedDigKey) || save.Ledger.CarriedDig <= 0)
        {
            return;
        }
        foreach (var zone in world.ShardZones)
        {
            if (zone != null && zone.FragmentKey == save.Ledger.CarriedDigKey && !Empty(save, zone))
            {
                zone.Excavation = Math.Min(100, Math.Max(zone.Excavation, save.Ledger.CarriedDig));
                GD.Print($"[Shardfall] The vault at {zone.Name} begins this timeline at excavation {zone.Excavation} of 100.");
            }
        }
        save.Ledger.CarriedDigKey = "";
        save.Ledger.CarriedDig = 0;
    }

    /// <summary>The vault holds its shard and can still be won this timeline. A live vault
    /// seeps, and a realm may seek it.</summary>
    public static bool Live(GuildSaveData save, ShardZone zone)
        => save != null && zone != null && !zone.ShardCollected && !Empty(save, zone) && !Sealed(save, zone);

    /// <summary>Why the gate will not open, in the player's words, or "" when it will.</summary>
    public static string GateShut(GuildSaveData save, ShardZone zone)
    {
        if (zone == null)
        {
            return "";
        }
        if (Empty(save, zone))
        {
            return $"The vault at {zone.Name} stands empty. Its shard is already yours.";
        }
        if (Claimed(zone))
        {
            return $"The vault at {zone.Name} is sealed. A rival realm has claimed its shard for this timeline. "
                   + "The shard will be in its vault again in the next.";
        }
        if (Sealed(save, zone))
        {
            return $"The vault at {zone.Name} is sealed. You have taken your shard for this timeline, "
                   + "and the others will not open until the next.";
        }
        return "";
    }

    /// <summary>The live vault that seeps into a province: the one whose gate stands in it.
    /// Null when the province is not seeped.</summary>
    public static ShardZone SeepOfProvince(GuildSaveData save, int provinceIndex)
    {
        var world = save?.Cycle?.World;
        if (world?.ShardZones == null || provinceIndex < 0 || !world.HasProvinces())
        {
            return null;
        }
        foreach (var zone in world.ShardZones)
        {
            if (zone != null && world.ProvinceIndexAt(zone.GateX, zone.GateY) == provinceIndex && Live(save, zone))
            {
                return zone;
            }
        }
        return null;
    }

    /// <summary>The live vault seeping into the province that holds a tile, or null.</summary>
    public static ShardZone SeepAt(GuildSaveData save, int x, int y)
    {
        var world = save?.Cycle?.World;
        return world == null ? null : SeepOfProvince(save, world.ProvinceIndexAt(x, y));
    }

    /// <summary>The province card's line about a vault in the province, or "".</summary>
    public static string CardLine(GuildSaveData save, Province province)
    {
        var world = save?.Cycle?.World;
        if (world?.ShardZones == null || province == null || !world.HasProvinces())
        {
            return "";
        }
        foreach (var zone in world.ShardZones)
        {
            if (zone == null || world.ProvinceIndexAt(zone.GateX, zone.GateY) != province.Index)
            {
                continue;
            }
            if (zone.ShardCollected)
            {
                return $"The shard of {zone.Name} was taken here this timeline.";
            }
            if (Empty(save, zone))
            {
                return $"The vault at {zone.Name} stands empty. Its shard is already yours.";
            }
            if (Claimed(zone))
            {
                return $"A rival realm has claimed the shard at {zone.Name} for this timeline. It is in its vault again in the next.";
            }
            if (Sealed(save, zone))
            {
                return $"The vault at {zone.Name} is sealed until the next timeline.";
            }
            // H2c: the excavation itself is the bar under this line on the card.
            string seeped = GroveAwake(save, zone)
                ? $"Seeped by the shard at {zone.Name}. Fights are stronger and sites pay more. A grove keeps the seep calm: nothing here hunts you, and no realm can garrison the vault."
                : $"Seeped by the shard at {zone.Name}. Fights are stronger, sites pay more, and everything here hunts you."
                  + (HasGrove(save, zone) ? " Its grove sleeps until the next timeline." : "");
            if (!string.IsNullOrEmpty(zone.HeldByKingdomId))
            {
                int left = Math.Max(1, ClaimMoons - zone.HeldMoons);
                seeped += $"\nA rival garrison holds the vault. It claims the shard in {left} moon(s) unless you beat it.";
            }
            return seeped;
        }
        return "";
    }
}
