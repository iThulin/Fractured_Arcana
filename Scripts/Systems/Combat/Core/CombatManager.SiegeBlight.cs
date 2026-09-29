using Godot;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

// ============================================================
// CombatManager.SiegeBlight.cs  (partial)
//
// Purpose:        What a portal strike is FOR (campus_building_upgrades_
//                 design_v1 §11): the attackers come to foul the guild's
//                 land, and they do it two ways.
//                   Raze     every guild building in the window gets a
//                            FOUNDATION: a structure unit standing at its
//                            edge (the gate door's pattern: team 0, so the
//                            enemy AI already hunts it, and IsStructure, so
//                            it is never a survivor and never commandable).
//                            A razed foundation breaks the building and fouls
//                            its ground.
//                   Ritual   one or two attackers (the longest reach) are
//                            BLIGHT RITUALISTS: they walk to a foundation and
//                            channel over it for two activations, telegraphed
//                            on its tile. A finished ritual fouls that
//                            building's ground. ANY damage breaks the channel
//                            (the warp channeler's counter-play, reused).
//                 Nothing here touches the save: results go to
//                 SiegeBlightReport, which the strategic layer reads when
//                 the fight returns (StrategicView.ConsumePortalStrikeReturn).
// Layer:          Combat / runtime
// Collaborators:  HexGridManager.BuildingStamps (where the buildings stand),
//                 Data/Units/building_foundation.json, SpawnRegistryUnit,
//                 CombatManager.EnemyIntents (planner + execute branches),
//                 CombatManager.Triggers (interrupt), HandleUnitDeath (raze),
//                 SiegeBlightReport (the hand-off), CampusBlight (applies it).
// ============================================================

public partial class CombatManager
{
    private const string FoundationUnitId = "building_foundation";

    /// <summary>How many attackers turn ritualist at the start of a strike.</summary>
    private const int RitualistCount = 2;

    /// <summary>Tiles from a foundation a ritualist may channel over it.</summary>
    private const int RitualRange = 3;

    /// <summary>Foundation unit → the guild building id it stands for.</summary>
    private readonly Dictionary<Unit, string> _foundations = new();

    /// <summary>The attackers channelling blight this fight.</summary>
    private readonly HashSet<Unit> _ritualists = new();

    /// <summary>Ritualist → the building id its current channel is fouling.</summary>
    private readonly Dictionary<Unit, string> _ritualTarget = new();

    private bool IsRitualist(Unit u) => u != null && _ritualists.Contains(u);

    // ── Setup ────────────────────────────────────────────────────────────

    /// <summary>Portal strike defense only: a foundation beside every stamped guild
    /// building (not the Sigil: the rift is the fight's centre, and its fate is the
    /// fight's outcome; not foundational buildings: the guild's heart is not razed
    /// in one fight), then pick the ritualists. Call after both sides are placed.</summary>
    private void SpawnBuildingFoundations()
    {
        _foundations.Clear();
        _ritualists.Clear();
        _ritualTarget.Clear();

        var siege = grid?.ActiveSiege;
        if (siege == null || !siege.Defending || siege.Vector != "PortalStrike")
            return;
        SiegeBlightReport.Begin();

        var done = new HashSet<string>();
        foreach (var (id, center, radius) in grid.BuildingStamps)
        {
            if (string.IsNullOrEmpty(id) || !done.Add(id) || id == TeleportSigil.Id)
                continue;
            var template = BuildingDatabase.GetTemplate(id);
            if (template == null || template.IsFoundational)
                continue;

            var tile = PickFoundationTile(center, radius);
            if (tile == null)
            {
                GD.Print($"[SiegeBlight] No free ground beside '{id}' for its foundation.");
                continue;
            }
            var f = SpawnRegistryUnit(FoundationUnitId, tile, teamId: 0);
            if (f == null)
                continue;
            f.IsStructure = true;
            f.IsPlayerControlled = false;   // commandable by nobody
            f.Name = $"{template.Name} Foundation";
            f.DisplayName = f.Name;
            f.RefreshNameLabel();
            _foundations[f] = id;
        }

        if (_foundations.Count == 0)
            return;

        // The ritualists: the attackers with the longest reach (they can channel
        // from behind the line), ties by name so the pick is stable.
        var pool = new List<Unit>();
        foreach (var e in enemyUnits)
            if (e != null && IsInstanceValid(e) && e.Stats.IsAlive && !e.IsStructure)
                pool.Add(e);
        pool.Sort((a, b) => a.AttackRange != b.AttackRange
            ? b.AttackRange.CompareTo(a.AttackRange)
            : string.CompareOrdinal(a.Name, b.Name));
        for (int i = 0; i < pool.Count && i < RitualistCount; i++)
        {
            _ritualists.Add(pool[i]);
            GD.Print($"[SiegeBlight] {pool[i].Name} carries the blight ritual.");
        }

        string msg = $"{_foundations.Count} building foundation(s) stand in the fight. The attackers mean to raze "
                   + "them and foul the ground: strike any ritualist mid-channel to break it.";
        GD.Print("[SiegeBlight] " + msg);
        combatUI?.AppendActionLog(msg);
    }

    /// <summary>The nearest free walkable tile at the stamp's edge (radius + 1),
    /// then one further out. Deterministic.</summary>
    private TileData PickFoundationTile(Vector2I center, int radius)
    {
        for (int ring = radius + 1; ring <= radius + 2; ring++)
        {
            TileData best = null;
            int bestKey = int.MaxValue;
            foreach (var kvp in grid.Tiles)
            {
                var td = kvp.Value;
                if (td == null || !td.IsWalkable || td.IsBlocked || td.IsOccupied)
                    continue;
                if (grid.Distance(kvp.Key, center) != ring)
                    continue;
                int key = (kvp.Key.X + 500) * 1000 + (kvp.Key.Y + 500);
                if (key < bestKey)
                {
                    bestKey = key;
                    best = td;
                }
            }
            if (best != null)
                return best;
        }
        return null;
    }

    private string FoundationNameFor(string buildingId)
    {
        var t = BuildingDatabase.GetTemplate(buildingId);
        return t != null ? t.Name : buildingId;
    }

    // ── Raze ─────────────────────────────────────────────────────────────

    /// <summary>HandleUnitDeath hook: a foundation fell.</summary>
    private void NoteFoundationDeath(Unit unit)
    {
        if (unit == null || !_foundations.TryGetValue(unit, out string id))
            return;
        _foundations.Remove(unit);
        SiegeBlightReport.Raze(id);
        string msg = $"The foundation of {FoundationNameFor(id)} is razed. The building above it cracks, and the ground goes sour.";
        GD.Print("[SiegeBlight] " + msg);
        combatUI?.AppendActionLog($"── {msg} ──");
    }

    // ── Ritualist planner ────────────────────────────────────────────────

    private Unit NearestFoundation(Unit enemy)
    {
        Unit best = null;
        int bestD = int.MaxValue;
        foreach (var kv in _foundations)
        {
            var f = kv.Key;
            if (f == null || !IsInstanceValid(f) || !f.Stats.IsAlive || f.CurrentTile == null)
                continue;
            int d = grid.Distance(enemy.CurrentTile.Axial, f.CurrentTile.Axial);
            if (d < bestD || (d == bestD && best != null && string.CompareOrdinal(f.Name, best.Name) < 0))
            {
                bestD = d;
                best = f;
            }
        }
        return best;
    }

    private EnemyIntent PlanBlightRitualist(Unit enemy)
    {
        // Charging: the ritual completes this activation, on the tile it locked.
        if (enemy.HasStatus("wizard_charging") && enemy.ChannelTile.HasValue)
        {
            var locked = enemy.ChannelTile.Value;
            return new EnemyIntent
            {
                Kind = IntentKind.Release,
                TargetTile = locked,
                ThreatTiles = { locked },
                Value = 0,
                BaseValue = 0,
            };
        }

        if (enemy.CurrentTile == null)
            return PlanSoldier(enemy);
        var target = NearestFoundation(enemy);
        if (target == null)
        {
            // Nothing left to foul: fight like anyone else.
            return enemy.AttackRange > 1 ? PlanRanger(enemy) : PlanSoldier(enemy);
        }

        var tile = target.CurrentTile.Axial;
        if (grid.Distance(enemy.CurrentTile.Axial, tile) <= RitualRange)
        {
            return new EnemyIntent
            {
                Kind = IntentKind.Channel,
                TargetUnit = target,
                TargetTile = tile,
                ThreatTiles = { tile },
                Value = 0,
                BaseValue = 0,
            };
        }

        // Out of reach: go to it (and strike it on arrival, which is razing too).
        int dmg = enemy.AttackDamage > 0 ? enemy.AttackDamage : 4;
        return new EnemyIntent
        {
            Kind = IntentKind.Attack,
            TargetUnit = target,
            TargetTile = tile,
            ThreatTiles = { tile },
            Value = dmg,
            BaseValue = dmg,
        };
    }

    // ── Ritualist executors ──────────────────────────────────────────────

    private async Task ExecuteRitualStart(Unit enemy, EnemyIntent intent)
    {
        if (!IsValidActor(enemy) || intent.TargetTile == null)
            return;
        var foundation = intent.TargetUnit;
        if (foundation == null || !_foundations.TryGetValue(foundation, out string id))
            return;

        enemy.ChannelTile = intent.TargetTile;
        enemy.ApplyStatus("wizard_charging", 2);
        _ritualTarget[enemy] = id;

        string msg = $"{enemy.Name} begins a blight ritual over the foundation of {FoundationNameFor(id)}. Strike them to break it!";
        GD.Print($"[SiegeBlight] {enemy.Name} channels blight into '{id}'.");
        combatUI?.AppendActionLog(msg);
        await ToSignal(GetTree().CreateTimer(0.35f), "timeout");
    }

    private async Task ExecuteRitualRelease(Unit enemy, EnemyIntent intent)
    {
        if (!IsValidActor(enemy))
            return;
        enemy.ChannelTile = null;
        enemy.RemoveStatus("wizard_charging");
        if (!_ritualTarget.TryGetValue(enemy, out string id))
            return;
        _ritualTarget.Remove(enemy);

        // The ground is the target, not the stone: a foundation razed mid-ritual
        // does not save the land under it.
        SiegeBlightReport.Foul(id);
        string msg = $"{enemy.Name} completes the ritual. Blight soaks into the ground under {FoundationNameFor(id)}.";
        GD.Print("[SiegeBlight] " + msg);
        combatUI?.AppendActionLog($"── {msg} ──");
        await ToSignal(GetTree().CreateTimer(0.35f), "timeout");
    }

    // ── Interrupt (called from HandleUnitStruck) ─────────────────────────

    /// <summary>Damage breaks a blight ritual, exactly as it collapses a warp rift.</summary>
    private void TryInterruptRitual(Unit struck, int hpLoss)
    {
        if (hpLoss <= 0 || !IsRitualist(struck))
            return;
        if (!struck.HasStatus("wizard_charging") || struck.ChannelTile == null)
            return;

        struck.ChannelTile = null;
        struck.RemoveStatus("wizard_charging");
        struck.CurrentIntent = null;
        struck.ClearIntentDisplay();
        _ritualTarget.Remove(struck);

        string msg = $"{struck.Name}'s blight ritual is broken!";
        GD.Print("[SiegeBlight] " + msg);
        combatUI?.AppendActionLog($"── {msg} ──");
    }
}

/// <summary>The hand-off from a portal-strike fight to the strategic layer: which
/// buildings' foundations were razed and how many rituals fouled each building's
/// ground. Runtime only (a quit mid-fight leaves the strike owed and this empty).
/// Begin() at fight setup; the strategic return reads it once and Clear()s it.</summary>
public static class SiegeBlightReport
{
    public static readonly List<string> Razed = new();
    public static readonly Dictionary<string, int> Fouled = new();

    /// <summary>True from a portal strike's setup until the strategic side reads it.</summary>
    public static bool Active { get; private set; }

    public static void Begin()
    {
        Clear();
        Active = true;
    }

    public static void Clear()
    {
        Razed.Clear();
        Fouled.Clear();
        Active = false;
    }

    public static void Raze(string buildingId)
    {
        if (!string.IsNullOrEmpty(buildingId) && !Razed.Contains(buildingId))
            Razed.Add(buildingId);
    }

    public static void Foul(string buildingId)
    {
        if (string.IsNullOrEmpty(buildingId))
            return;
        Fouled.TryGetValue(buildingId, out int n);
        Fouled[buildingId] = n + 1;
    }
}
