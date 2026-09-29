using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// CampusBlight.cs
//
// Purpose:        Corrupted campus land (campus_building_upgrades_design_v1
//                 §11). An enemy siege does not only break buildings: it
//                 poisons the ground they stand on. Corruption lives on
//                 the campus TILES (CampusTileSaveData.Corruption, 0-3),
//                 so it outlasts the building above it: tear a blighted
//                 hall down and rebuild it on the same spot, and it is
//                 still blighted. A building's level is the worst tile
//                 under its footprint, cached on BuildingSaveData.BlightLevel.
//
//                   1 Tainted   the ground draws off splinters each moon
//                   2 Blighted  the chartered doctrine goes dark, and the
//                               building's blighted form takes over (a
//                               different way of working, not a penalty
//                               table: see the design doc §11c)
//                   3 Overrun   the building stops working (IsFunctional)
//
//                 Blighted ground creeps: every moon a blighted or overrun
//                 patch taints one clean neighbouring tile. Cleanse Land
//                 lowers a footprint one step for materials and splinters.
//                 Destroyed buildings leave Rubble on their footprint, which
//                 is cleared from the destroyed building's card.
// Layer:          System (campus)
// Collaborators:  CampusMapSaveData (tiles), BuildingSaveData (BlightLevel,
//                 Q/R/Rotation), CampusGridManager.GetFootprintHexes,
//                 BuildingEffectApplier (recompute when a building is
//                 overrun or recovers), TeleportSigil + StrategicView.
//                 PortalStrike (the sources), CampusScreen and
//                 HomeBuildingPanelHost (the Cleanse and Clear buttons).
// ============================================================

/// <summary>Campus land corruption. Stateless: everything lives on the save.</summary>
public static class CampusBlight
{
    public const int MaxLevel = 3;

    /// <summary>Foundational buildings (the Grand Hall and its kind) are the
    /// guild's heart: their land can be blighted, never overrun.</summary>
    public const int FoundationalCap = 2;

    /// <summary>Splinters each tainted-or-worse building draws off per moon.</summary>
    public const int DrainSplintersPerBuilding = 1;

    /// <summary>Cleanse Land, per step, scaled by the level being lifted.</summary>
    public const int CleanseMaterialsPerLevel = 20;
    public const int CleanseSplintersPerLevel = 5;

    /// <summary>Materials to clear one tile of rubble.</summary>
    public const int RubbleMaterialsPerTile = 8;

    /// <summary>Raised after anything changes corruption or rubble on the campus, so
    /// a live campus grid redraws its placeholder blight visuals (CampusGridManager
    /// .RefreshBlight) without a reload.</summary>
    public static event Action Changed;

    private static void RaiseChanged() => Changed?.Invoke();

    private static readonly (int dq, int dr)[] Dirs =
        { (1, 0), (-1, 0), (0, 1), (0, -1), (1, -1), (-1, 1) };

    // ── Reads ────────────────────────────────────────────────────────────

    public static string LevelName(int level) => level switch
    {
        1 => "Tainted",
        2 => "Blighted",
        >= 3 => "Overrun",
        _ => "Clean",
    };

    public static BuildingSaveData Find(GuildSaveData save, string buildingId)
    {
        if (save?.Buildings == null)
        {
            return null;
        }
        foreach (var b in save.Buildings)
        {
            if (b != null && b.Id == buildingId)
            {
                return b;
            }
        }
        return null;
    }

    /// <summary>The building's cached blight level (0 when unknown).</summary>
    public static int Level(GuildSaveData save, string buildingId) => Find(save, buildingId)?.BlightLevel ?? 0;

    /// <summary>Blighted (level 2) or worse: the doctrine is dark and the
    /// blighted form is in force. An overrun building is not functional at all,
    /// so its blighted form never fires either (callers check IsFunctional first).</summary>
    public static bool IsBlighted(GuildSaveData save, string buildingId) => Level(save, buildingId) >= 2;

    private static CampusMapSaveData Map(GuildSaveData save) => save?.Ledger?.CampusMap;

    private static Dictionary<(int, int), CampusTileSaveData> TileIndex(CampusMapSaveData map)
    {
        var index = new Dictionary<(int, int), CampusTileSaveData>();
        if (map?.Tiles == null)
        {
            return index;
        }
        foreach (var t in map.Tiles)
        {
            if (t != null)
            {
                index[(t.Q, t.R)] = t;
            }
        }
        return index;
    }

    /// <summary>The campus tiles under a building's footprint, sited or not (a
    /// destroyed building keeps its last Q/R/Rotation). Tiles off the grid are skipped.</summary>
    public static List<CampusTileSaveData> FootprintTiles(GuildSaveData save, BuildingSaveData b)
    {
        var list = new List<CampusTileSaveData>();
        var map = Map(save);
        if (b == null || map == null)
        {
            return list;
        }
        // Unsited and not destroyed (bought but never placed, or rebuilt and not yet
        // re-sited): its Q/R mean nothing. A destroyed building keeps integrity 0
        // until it is rebuilt, and its Q/R still mark where it stood.
        if (!b.IsPlaced && b.CurrentIntegrity > 0)
        {
            return list;
        }
        var index = TileIndex(map);
        var template = BuildingDatabase.GetTemplate(b.Id);
        foreach (var hex in CampusGridManager.GetFootprintHexes(template, new Vector2I(b.Q, b.R), b.Rotation))
        {
            if (index.TryGetValue((hex.X, hex.Y), out var tile))
            {
                list.Add(tile);
            }
        }
        return list;
    }

    // ── Recompute ────────────────────────────────────────────────────────

    /// <summary>Refresh every building's cached level from the tiles. When any
    /// building crosses into or out of Overrun, campus effects are recomputed,
    /// because IsFunctional changed under them.</summary>
    public static void Recompute(GuildSaveData save)
    {
        if (save?.Buildings == null)
        {
            return;
        }
        bool functionalChanged = false;
        foreach (var b in save.Buildings)
        {
            if (b == null)
            {
                continue;
            }
            bool wasFunctional = b.IsFunctional;
            int level = 0;
            if (b.IsPlaced)
            {
                foreach (var t in FootprintTiles(save, b))
                {
                    level = Math.Max(level, t.Corruption);
                }
            }
            var template = BuildingDatabase.GetTemplate(b.Id);
            if (template != null && template.IsFoundational)
            {
                level = Math.Min(level, FoundationalCap);
            }
            b.BlightLevel = Math.Clamp(level, 0, MaxLevel);
            if (b.IsFunctional != wasFunctional)
            {
                functionalChanged = true;
            }
        }
        if (functionalChanged)
        {
            BuildingEffectApplier.ApplyCampusEffects(save);
        }
    }

    // ── Corruption sources ───────────────────────────────────────────────

    /// <summary>Corrupt the ground under a building by <paramref name="steps"/>
    /// (every footprint tile, capped at Overrun). Returns the report line, "" when
    /// the building has no ground on the campus.</summary>
    public static string CorruptBuildingLand(GuildSaveData save, string buildingId, int steps)
    {
        var b = Find(save, buildingId);
        if (b == null || steps <= 0)
        {
            return "";
        }
        var tiles = FootprintTiles(save, b);
        if (tiles.Count == 0)
        {
            return "";
        }
        // A standing building reads its cached level; the ground a destroyed one
        // left reads the tiles themselves (its cached level is 0 once unsited).
        int before = b.IsPlaced ? b.BlightLevel : WorstOf(tiles);
        foreach (var t in tiles)
        {
            t.Corruption = Math.Min(MaxLevel, t.Corruption + steps);
        }
        Recompute(save);
        SaveManager.MarkDirty();
        RaiseChanged();
        int after = b.IsPlaced ? b.BlightLevel : WorstOf(tiles);
        if (!b.IsPlaced)
        {
            return after > before
                ? $"The ground where {b.Name} stood is {LevelName(after).ToLowerInvariant()}."
                : "";
        }
        if (after <= before)
        {
            return $"The ground under {b.Name} is fouled further, though it can sink no lower.";
        }
        return after switch
        {
            1 => $"The ground under {b.Name} is tainted.",
            2 => $"The ground under {b.Name} is blighted. Its doctrine goes dark and it works in the blight's way now.",
            _ => $"The ground under {b.Name} is overrun. It has stopped working.",
        };
    }

    private static int WorstOf(List<CampusTileSaveData> tiles)
    {
        int worst = 0;
        foreach (var t in tiles)
        {
            worst = Math.Max(worst, t.Corruption);
        }
        return worst;
    }

    // ── Cleanse Land ─────────────────────────────────────────────────────

    /// <summary>(materials, splinters) to lift this building's land one step.</summary>
    public static (int materials, int splinters) CleanseCost(BuildingSaveData b)
    {
        int level = b?.BlightLevel ?? 0;
        return (level * CleanseMaterialsPerLevel, level * CleanseSplintersPerLevel);
    }

    public static string CannotCleanseReason(GuildSaveData save, BuildingSaveData b)
    {
        if (save == null || b == null)
        {
            return "No save.";
        }
        if (b.BlightLevel <= 0)
        {
            return "The ground is clean.";
        }
        var (mats, splinters) = CleanseCost(b);
        if (save.BuildMaterials < mats)
        {
            return $"Cleansing needs {mats} materials.";
        }
        if (save.ArcaneSplinters < splinters)
        {
            return $"Cleansing needs {splinters} splinters.";
        }
        return null;
    }

    /// <summary>Lower every footprint tile one step. Returns the report line or null.</summary>
    public static string TryCleanse(GuildSaveData save, string buildingId)
    {
        var b = Find(save, buildingId);
        if (CannotCleanseReason(save, b) != null)
        {
            return null;
        }
        var (mats, splinters) = CleanseCost(b);
        save.BuildMaterials -= mats;
        save.ArcaneSplinters -= splinters;
        // A foundational building reads at most Blighted, so ground deeper than that
        // under it is first brought to the cap: one paid step always shows.
        var template = BuildingDatabase.GetTemplate(b.Id);
        int cap = template != null && template.IsFoundational ? FoundationalCap : MaxLevel;
        foreach (var t in FootprintTiles(save, b))
        {
            t.Corruption = Math.Max(0, Math.Min(t.Corruption, cap) - 1);
        }
        Recompute(save);
        SaveManager.Save();
        RaiseChanged();
        GD.Print($"[CampusBlight] {b.Name} cleansed to {LevelName(b.BlightLevel)} ({mats}m, {splinters} splinters).");
        return b.BlightLevel == 0
            ? $"The ground under {b.Name} is clean again."
            : $"The ground under {b.Name} lifts to {LevelName(b.BlightLevel).ToLowerInvariant()}.";
    }

    // ── Rubble ───────────────────────────────────────────────────────────

    /// <summary>A building was destroyed: its footprint becomes Rubble, unbuildable
    /// until cleared. Call once, the moment ApplyDamage reports the destruction.</summary>
    public static void MarkRubble(GuildSaveData save, BuildingSaveData b)
    {
        if (b == null)
        {
            return;
        }
        foreach (var t in FootprintTiles(save, b))
        {
            if (t.Ground == "Rubble")
            {
                continue;
            }
            t.PriorGround = t.Ground;
            t.Ground = "Rubble";
            t.RubbleOf = b.Id;
        }
        SaveManager.MarkDirty();
        RaiseChanged();
    }

    /// <summary>Rubble tiles a destroyed building left behind.</summary>
    public static List<CampusTileSaveData> RubbleTiles(GuildSaveData save, string buildingId)
    {
        var list = new List<CampusTileSaveData>();
        var map = Map(save);
        if (map?.Tiles == null || string.IsNullOrEmpty(buildingId))
        {
            return list;
        }
        foreach (var t in map.Tiles)
        {
            if (t != null && t.Ground == "Rubble" && t.RubbleOf == buildingId)
            {
                list.Add(t);
            }
        }
        return list;
    }

    public static int RubbleCost(GuildSaveData save, string buildingId)
        => RubbleTiles(save, buildingId).Count * RubbleMaterialsPerTile;

    public static string CannotClearRubbleReason(GuildSaveData save, string buildingId)
    {
        int cost = RubbleCost(save, buildingId);
        if (save == null || cost <= 0)
        {
            return "No rubble.";
        }
        if (save.BuildMaterials < cost)
        {
            return $"Clearing needs {cost} materials.";
        }
        return null;
    }

    /// <summary>Clear a destroyed building's rubble. Corruption stays: rubble is
    /// broken stone, blight is poisoned ground.</summary>
    public static bool TryClearRubble(GuildSaveData save, string buildingId)
    {
        if (CannotClearRubbleReason(save, buildingId) != null)
        {
            return false;
        }
        int cost = RubbleCost(save, buildingId);
        save.BuildMaterials -= cost;
        foreach (var t in RubbleTiles(save, buildingId))
        {
            t.Ground = string.IsNullOrEmpty(t.PriorGround) ? "Lawn" : t.PriorGround;
            t.PriorGround = "";
            t.RubbleOf = "";
        }
        SaveManager.Save();
        RaiseChanged();
        GD.Print($"[CampusBlight] Rubble of '{buildingId}' cleared for {cost} materials.");
        return true;
    }

    // ── The moon ─────────────────────────────────────────────────────────

    /// <summary>Lunation tick: tainted ground drains splinters, and every blighted
    /// patch taints one clean neighbouring tile (deterministic). Returns report lines.</summary>
    public static List<string> TickLunation(CycleState cycle, GuildSaveData save)
    {
        var lines = new List<string>();
        var map = Map(save);
        if (cycle == null || save?.Buildings == null || map?.Tiles == null)
        {
            return lines;
        }
        Recompute(save);
        var index = TileIndex(map);
        int lunation = cycle.Calendar?.CurrentLunation ?? 1;

        // Drain: every standing building on tainted-or-worse ground.
        int drained = 0;
        foreach (var b in save.Buildings)
        {
            if (b != null && b.Tier > 0 && b.IsPlaced && b.BlightLevel >= 1)
            {
                drained += DrainSplintersPerBuilding;
            }
        }
        drained = Math.Min(drained, Math.Max(0, save.ArcaneSplinters));
        if (drained > 0)
        {
            save.ArcaneSplinters -= drained;
            lines.Add($"The fouled ground under the campus draws off {drained} splinter(s).");
        }

        // Creep: one clean neighbour per blighted patch. A patch is a building's
        // footprint, or the rubble it left.
        var sources = new List<(string id, List<CampusTileSaveData> tiles)>();
        foreach (var b in save.Buildings)
        {
            if (b == null)
            {
                continue;
            }
            var tiles = b.IsPlaced && b.Tier > 0 ? FootprintTiles(save, b) : RubbleTiles(save, b.Id);
            int worst = 0;
            foreach (var t in tiles)
            {
                worst = Math.Max(worst, t.Corruption);
            }
            if (worst >= 2)
            {
                sources.Add((b.Id, tiles));
            }
        }
        sources.Sort((a, c) => string.CompareOrdinal(a.id, c.id));

        var tainted = new List<string>();
        foreach (var (id, tiles) in sources)
        {
            var own = new HashSet<(int, int)>();
            foreach (var t in tiles)
            {
                own.Add((t.Q, t.R));
            }
            var candidates = new List<CampusTileSaveData>();
            var seen = new HashSet<(int, int)>();
            foreach (var t in tiles)
            {
                foreach (var (dq, dr) in Dirs)
                {
                    var key = (t.Q + dq, t.R + dr);
                    if (own.Contains(key) || !seen.Add(key))
                    {
                        continue;
                    }
                    if (index.TryGetValue(key, out var n) && n.Corruption == 0)
                    {
                        candidates.Add(n);
                    }
                }
            }
            if (candidates.Count == 0)
            {
                continue;
            }
            candidates.Sort((a, c) => a.Q != c.Q ? a.Q.CompareTo(c.Q) : a.R.CompareTo(c.R));
            var pick = candidates[RngFor(cycle, id, lunation).Next(candidates.Count)];
            pick.Corruption = 1;
            string above = BuildingOn(save, pick);
            if (!string.IsNullOrEmpty(above))
            {
                tainted.Add(above);
            }
        }
        Recompute(save);
        if (sources.Count > 0)
        {
            lines.Add(tainted.Count > 0
                ? $"The blight creeps across the campus. It reaches the ground under {string.Join(", ", tainted)}."
                : "The blight creeps a little further across the campus grounds.");
        }
        if (drained > 0 || sources.Count > 0)
        {
            SaveManager.MarkDirty();
            RaiseChanged();
        }
        return lines;
    }

    /// <summary>The name of the standing building over this tile, or "".</summary>
    private static string BuildingOn(GuildSaveData save, CampusTileSaveData tile)
    {
        foreach (var b in save.Buildings)
        {
            if (b == null || !b.IsPlaced || b.Tier <= 0)
            {
                continue;
            }
            foreach (var t in FootprintTiles(save, b))
            {
                if (ReferenceEquals(t, tile))
                {
                    return b.Name;
                }
            }
        }
        return "";
    }

    private static Random RngFor(CycleState cycle, string salt, int lunation)
    {
        unchecked
        {
            int seed = (cycle.WorldSeed * 397) ^ (lunation * 7919);
            foreach (char ch in salt ?? "")
            {
                seed = (seed * 31) ^ ch;
            }
            return new Random(seed);
        }
    }
}
