using System;
using System.Collections.Generic;

// ============================================================
// ProvinceField.cs
//
// Purpose:        Fills a province with sites the first time a
//                 field party deploys into it (docs/
//                 province_field_v1.md, stage P3, rule F5). The
//                 world's own scatter leaves a province with one
//                 to three places. The field wants it dense: about
//                 one site for every six tiles, a third of them
//                 fights, the rest resolved in a click or a short
//                 panel. The fill runs once per province and is
//                 saved with the world (Province.FieldFilled), so a
//                 timeline already in play gains its sites as the
//                 party reaches each province.
// Layer:          Data
// Collaborators:  WorldData.cs (Pois, Tiles, ProvinceMap),
//                 Province.cs (FieldFilled), ExpeditionManager.cs
//                 (calls Fill before the window is built, resolves
//                 the Stash and Shrine kinds)
// See:            docs/province_field_v1.md sections 2 and 5
// ============================================================

/// <summary>The field's site fill. Plain data work; no Godot types.</summary>
public static class ProvinceField
{
    /// <summary>One site for this many tiles of province.</summary>
    public const int TilesPerSite = 6;
    public const int MinSites = 8;
    public const int MaxSites = 20;

    /// <summary>No two sites closer than this, in hexes.</summary>
    public const int MinSpacing = 2;

    /// <summary>The fill's mix, drawn in a shuffled order and repeated as needed. Six
    /// fights in twenty; the fight cap below holds the share at a third either way.</summary>
    private static readonly PoiKind[] _mix =
    {
        PoiKind.Combat, PoiKind.Combat, PoiKind.Combat, PoiKind.Combat, PoiKind.Combat, PoiKind.Combat,
        PoiKind.Narrative, PoiKind.Narrative, PoiKind.Narrative, PoiKind.Narrative,
        PoiKind.Stash, PoiKind.Stash, PoiKind.Stash, PoiKind.Stash,
        PoiKind.Shrine, PoiKind.Shrine,
        PoiKind.Rest, PoiKind.Rest,
        PoiKind.Negotiation, PoiKind.Negotiation,
    };

    /// <summary>How many sites a province of this size should hold.</summary>
    public static int TargetFor(int tileCount)
        => Math.Clamp((int)Math.Round(tileCount / (float)TilesPerSite), MinSites, MaxSites);

    /// <summary>Fills the province holding (col, row), once. Returns the number of sites
    /// added: 0 when it was already filled, holds no province, or had no room.</summary>
    public static int Fill(WorldData world, int col, int row)
    {
        if (world == null || !world.HasProvinces())
        {
            return 0;
        }
        var province = world.ProvinceAt(col, row);
        if (province == null || province.FieldFilled)
        {
            return 0;
        }
        province.FieldFilled = true;

        // The province's open ground, and the places already standing in it.
        var open = new List<(int x, int y)>();
        var taken = new List<(int x, int y)>();
        int standing = 0;
        int fights = 0;
        for (int y = 0; y < world.Height; y++)
        {
            for (int x = 0; x < world.Width; x++)
            {
                if (world.ProvinceIndexAt(x, y) != province.Index)
                {
                    continue;
                }
                var t = world.GetTile(x, y);
                if (t.PoiIndex >= 0)
                {
                    taken.Add((x, y));
                    var p = t.PoiIndex < world.Pois.Count ? world.Pois[t.PoiIndex] : null;
                    if (p != null && !p.Consumed)
                    {
                        standing++;
                        if (p.Kind == PoiKind.Combat || p.Kind == PoiKind.Prison)
                        {
                            fights++;
                        }
                    }
                    continue;
                }
                if (t.IsLand && t.SettlementIndex < 0 && t.ShardZoneIndex < 0 && !t.IsStagingPoint)
                {
                    open.Add((x, y));
                }
            }
        }

        // The party's own arrival tile stays clear.
        taken.Add((col, row));

        int target = TargetFor(province.TileCount > 0 ? province.TileCount : open.Count);
        int maxFights = Math.Max(2, target / 3);
        var rng = new Random(unchecked(province.Index * 7919 + world.Width * 104729 + 17));

        var deck = new List<PoiKind>(_mix);
        for (int i = deck.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (deck[i], deck[j]) = (deck[j], deck[i]);
        }

        int added = 0;
        int draw = 0;
        int attempts = 0;
        int maxAttempts = target * 40;
        while (standing + added < target && open.Count > 0 && attempts < maxAttempts)
        {
            attempts++;
            int pick = rng.Next(open.Count);
            var (x, y) = open[pick];
            open[pick] = open[open.Count - 1];
            open.RemoveAt(open.Count - 1);
            if (TooClose(taken, x, y))
            {
                continue;
            }

            PoiKind kind = deck[draw % deck.Count];
            draw++;
            if (kind == PoiKind.Combat && fights >= maxFights)
            {
                kind = PoiKind.Stash;
            }
            if (kind == PoiKind.Combat)
            {
                fights++;
            }

            var tile = world.GetTile(x, y);
            tile.PoiIndex = world.Pois.Count;
            world.Pois.Add(new WorldPoi
            {
                X = x,
                Y = y,
                Kind = kind,
                KingdomId = tile.KingdomId ?? "",
                Discovered = false,
                GrantsStaging = false,
            });
            world.SetTile(x, y, tile);
            taken.Add((x, y));
            added++;
        }
        return added;
    }

    private static bool TooClose(List<(int x, int y)> taken, int x, int y)
    {
        foreach (var (tx, ty) in taken)
        {
            if (HexCoord.OffsetDistance(tx, ty, x, y) < MinSpacing)
            {
                return true;
            }
        }
        return false;
    }
}
