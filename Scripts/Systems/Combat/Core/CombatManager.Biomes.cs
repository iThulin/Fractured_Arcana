using Godot;
using System.Collections.Generic;

// ============================================================
// CombatManager.Biomes.cs
//
// Purpose:        Combat overhaul stage C4: applies the fight's
//                 biome kit. The kit's events join the map events
//                 the grid already runs, its objects are placed on
//                 the field, and its rules are written to the log in
//                 plain words so the player is told what the ground
//                 does before it does it.
// Layer:          Combat / runtime
// Collaborators:  BiomeKits.cs (data), CombatManager.cs
//                   (ConfigureAndGenerateMap, the log replay in
//                   _Ready), CombatManager.MapObjects.cs
//                   (SpawnMapObject), HexGridManager.Recipes.cs
//                   (InjectedMapEvents, ResolveRecipeCoord)
// See:            docs/combat_c4_biome_kits_v1.md
// ============================================================

public partial class CombatManager
{
    private BiomeKit _biomeKit;
    private bool _biomeRulesLogged;

    /// <summary>The kit applies to field fights on its terrain. A scripted fight is
    /// exactly as authored, and a siege is fought on a compiled city map.</summary>
    private BiomeKit ResolveBiomeKit()
    {
        if (grid == null || ScriptedFight || grid.ActiveSiege != null)
            return null;
        return BiomeKits.For(EncounterContextCarrier.SourceTerrain);
    }

    /// <summary>From ConfigureAndGenerateMap, after the pressure add-on and before the
    /// recipe's objects are placed. Adds the kit's events to the injected list.</summary>
    private void ApplyBiomeKit()
    {
        _biomeKit = ResolveBiomeKit();
        if (_biomeKit == null)
            return;

        var events = BiomeKits.BuildEvents(_biomeKit, grid.ActiveMapEvents);
        foreach (var ev in events)
            grid.InjectedMapEvents.Add(ev);
        GD.Print($"[Biome] {_biomeKit.Name}: {events.Count} event(s) added " +
                 $"({_biomeKit.Events.Count - events.Count} left out because the map has its own).");
    }

    /// <summary>After the recipe's own objects. A kit object is skipped when the field
    /// already holds one of its kind near that spot, so a map that authors a brazier
    /// does not get a second beside it.</summary>
    private void SpawnBiomeKitObjects()
    {
        if (_biomeKit == null || grid == null)
            return;

        int placed = 0;
        foreach (var (kind, at) in _biomeKit.Objects)
        {
            Vector2I coord = grid.ResolveRecipeCoord(at);
            if (FieldObjectNear(kind, coord, 2))
                continue;

            var candidates = new List<Vector2I> { coord };
            candidates.AddRange(grid.GetNeighbors(coord));
            foreach (var c in candidates)
            {
                if (SpawnMapObject(kind, grid.GetTile(c)) != null)
                {
                    placed++;
                    break;
                }
            }
        }
        GD.Print($"[Biome] {_biomeKit.Name}: {placed} object(s) placed.");
    }

    private bool FieldObjectNear(string kind, Vector2I coord, int within)
    {
        foreach (var obj in fieldObjects)
        {
            if (obj == null || !IsInstanceValid(obj) || obj.CurrentTile == null)
                continue;
            if (obj.MapObjectKind != kind)
                continue;
            if (grid.Distance(obj.CurrentTile.Axial, coord) <= within)
                return true;
        }
        return false;
    }

    /// <summary>From _Ready, once the combat UI is found: the map was generated before
    /// the log existed, so the rules are written here. Once per fight.</summary>
    private void LogBiomeKitRules()
    {
        if (_biomeRulesLogged || _biomeKit == null || combatUI == null)
            return;
        _biomeRulesLogged = true;
        combatUI.AppendActionLog($"── {_biomeKit.Name} ──");
        foreach (string line in _biomeKit.Rules)
            combatUI.AppendActionLog(line);
    }
}
