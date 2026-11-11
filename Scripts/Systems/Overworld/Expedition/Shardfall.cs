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
           && TakenThisTimeline(save.Cycle);

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
            if (Sealed(save, zone))
            {
                return $"The vault at {zone.Name} is sealed until the next timeline.";
            }
            // H2c: the excavation itself is the bar under this line on the card.
            return $"Seeped by the shard at {zone.Name}. Fights are stronger, sites pay more, and everything here hunts you.";
        }
        return "";
    }
}
