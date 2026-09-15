using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// HexGridManager.Tactics.cs
//
// Purpose:        Post-generation tactical report. Answers "will the
//                 player just kite this?" with two numbers: how much of
//                 the enemy deployment the player deployment can see,
//                 and how much of the open ground has cover beside it.
//                 Logged every generation; warned against the recipe's
//                 TacticsSpec when one is authored.
// Layer:          Systems / Combat / Terrain (partial of HexGridManager)
// Collaborators:  HexGridManager.Cover.cs (HasAnyCover),
//                 HexGridManager.Pathfinding.cs (HasLineOfSight),
//                 MapRecipe.TacticsSpec
// See:            docs/cover_and_zoc_v1.md §7.6
// ============================================================

public partial class HexGridManager
{
    /// <summary>Fraction of player-zone x enemy-zone tile pairs with clear sight.
    /// 1.0 on a bare field; a courtyard with a ring wall lands well under 0.5.</summary>
    public float TacticalVisibility { get; private set; } = -1f;

    /// <summary>Fraction of open, unreserved tiles with cover on at least one side.</summary>
    public float TacticalCoverFraction { get; private set; } = -1f;

    /// <summary>Count of open tiles, for the report line.</summary>
    public int TacticalOpenTiles { get; private set; }

    private void ComputeTacticalMetrics()
    {
        // Visibility across the deployment zones.
        List<Vector2I> playerTiles = null, enemyTiles = null;
        foreach (var z in SpawnZones)
        {
            if (z.Side == SpawnSide.Player) playerTiles = z.Tiles;
            else if (z.Side == SpawnSide.Enemy) enemyTiles = z.Tiles;
        }

        int pairs = 0, seen = 0;
        if (playerTiles != null && enemyTiles != null)
        {
            foreach (var p in playerTiles)
                foreach (var e in enemyTiles)
                {
                    pairs++;
                    if (HasLineOfSight(p, e))
                        seen++;
                }
        }
        TacticalVisibility = pairs > 0 ? (float)seen / pairs : -1f;

        // Cover adjacency over the open ground.
        int open = 0, covered = 0;
        foreach (var kv in Tiles)
        {
            var t = kv.Value;
            if (t == null || !t.IsWalkable || t.IsBlocked || IsReserved(kv.Key))
                continue;
            open++;
            if (HasAnyCover(kv.Key))
                covered++;
        }
        TacticalOpenTiles = open;
        TacticalCoverFraction = open > 0 ? (float)covered / open : -1f;

        string recipeId = _activeRecipe?.Id ?? LayoutType.ToString();
        GD.Print($"[Tactics] {recipeId} seed {MapSeed}: visibility {TacticalVisibility:0.00} " +
                 $"({seen}/{pairs} zone pairs), cover {TacticalCoverFraction:0.00} ({covered}/{open} open tiles).");

        var spec = _activeRecipe?.Tactics;
        if (spec == null)
            return;
        if (TacticalVisibility >= 0f && TacticalVisibility > spec.MaxVisibility)
            GD.PushWarning($"[Tactics] {recipeId}: visibility {TacticalVisibility:0.00} exceeds max {spec.MaxVisibility:0.00}. The deployments see each other; expect kiting.");
        if (TacticalCoverFraction >= 0f && TacticalCoverFraction < spec.MinCover)
            GD.PushWarning($"[Tactics] {recipeId}: cover {TacticalCoverFraction:0.00} under min {spec.MinCover:0.00}. Add a cover_line or thicken the skeleton.");
    }
}
