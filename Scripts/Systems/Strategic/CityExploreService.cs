using System.Collections.Generic;
using Godot;

// ============================================================
// CityExploreService.cs
//
// Purpose:        Generate + look up per-city district content for the
//                 explore mechanic. Content is assigned deterministically
//                 from a per-city seed (so a regenerated state matches) and
//                 persisted into CycleState.CityExplore on first visit.
// Layer:          Systems (Strategic)
// Collaborators:  CityExploreState.cs (model), CycleState.cs (storage),
//                 WorldAtlas3D.cs (reveal + markers).
// ============================================================

/// <summary>Stateless helper: get-or-generate a city's explore content and find
/// individual districts. The centre district (0,0) is always the Service hub and
/// starts revealed; the rest are a deterministic weighted mix of Event / Fight /
/// Story / Empty.</summary>
public static class CityExploreService
{
    // Content weights for non-centre districts (must sum to 100).
    private const int WeightEvent = 35;
    private const int WeightFight = 25;   // cumulative 60
    private const int WeightStory = 15;   // cumulative 75
    // remaining 25 → Empty

    /// <summary>Stable per-city id within a cycle. Center coords + kingdom are fixed
    /// for a settlement once the world is generated.</summary>
    public static string CityId(WorldSettlement city)
        => city == null ? "" : $"{city.KingdomId}:{city.CenterX},{city.CenterY}";

    /// <summary>Existing explore state for a city id, or null if never visited.</summary>
    public static CityExploreState Get(CycleState cycle, string cityId)
    {
        if (cycle == null || string.IsNullOrEmpty(cityId)) return null;
        foreach (var s in cycle.CityExplore)
            if (s.CityId == cityId) return s;
        return null;
    }

    /// <summary>Fetch the city's explore state, generating + persisting it on first
    /// visit. <paramref name="districtDeltas"/> are the axial deltas from the city
    /// centre (the same set WorldAtlas3D renders as flower districts).</summary>
    public static CityExploreState GetOrGenerate(CycleState cycle, WorldSettlement city,
                                                 IEnumerable<Vector2I> districtDeltas)
    {
        if (cycle == null || city == null) return null;
        string id = CityId(city);

        var state = Get(cycle, id);
        if (state != null && state.Generated) return state;
        if (state == null)
        {
            state = new CityExploreState { CityId = id };
            cycle.CityExplore.Add(state);
        }

        uint baseSeed = Hash(id);
        state.Districts.Clear();
        foreach (var d in districtDeltas)
        {
            var entry = new CityDistrictEntry { Dq = d.X, Dr = d.Y };
            if (d.X == 0 && d.Y == 0)
            {
                entry.Content = (int)DistrictContentType.Service;
                entry.Revealed = true;   // the seat you arrive at is already scouted
            }
            else
            {
                entry.Content = (int)PickContent(baseSeed, d);
            }
            state.Districts.Add(entry);
        }
        state.Generated = true;
        return state;
    }

    /// <summary>The entry for a district (axial delta), or null.</summary>
    public static CityDistrictEntry FindDistrict(CityExploreState state, Vector2I district)
    {
        if (state == null) return null;
        foreach (var e in state.Districts)
            if (e.Dq == district.X && e.Dr == district.Y) return e;
        return null;
    }

    /// <summary>Deterministic content pick for a non-centre district: a small integer
    /// hash of the city seed + district coords, mapped through the weight table.</summary>
    private static DistrictContentType PickContent(uint baseSeed, Vector2I d)
    {
        uint h = baseSeed ^ (uint)(d.X * 73856093) ^ (uint)(d.Y * 19349663);
        h ^= h >> 13; h *= 2654435761u; h ^= h >> 16;
        int roll = (int)(h % 100u);
        if (roll < WeightEvent) return DistrictContentType.Event;
        if (roll < WeightEvent + WeightFight) return DistrictContentType.Fight;
        if (roll < WeightEvent + WeightFight + WeightStory) return DistrictContentType.Story;
        return DistrictContentType.Empty;
    }

    /// <summary>FNV-1a over the city id: a stable per-city seed.</summary>
    private static uint Hash(string s)
    {
        uint h = 2166136261u;
        foreach (char c in s) { h ^= c; h *= 16777619u; }
        return h;
    }
}
