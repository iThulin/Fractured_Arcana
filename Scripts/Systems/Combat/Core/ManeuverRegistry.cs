using System;
using System.Collections.Generic;
using System.Text.Json;
using Godot;

// ============================================================
// ManeuverRegistry.cs
//
// Purpose:        Loads Data/Maneuvers/*.json into ManeuverDefinition
//                 and validates every entry at load (spec v1 §8,
//                 "Validator" row): a maneuver with an unknown effect
//                 key, an unknown weapon class, an unknown shape or an
//                 Edge cost above the cap is refused with a PushError
//                 and never reaches the tray. Same pattern as
//                 StanceRegistry and ItemDatabase.
// Layer:          Data
// Collaborators:  ManeuverDefinition.cs, CombatManager.Maneuvers.cs
//                 (the consumer of ForWeaponClass), TechniqueTray.cs
// ============================================================

public static class ManeuverRegistry
{
    private const string MANEUVERS_DIR = "res://Data/Maneuvers/";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        IncludeFields = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Every effect key CombatManager.Maneuvers.cs resolves. A JSON key
    /// outside this set fails validation. Extend both together.</summary>
    public static readonly HashSet<string> KnownEffectKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "damage", "push", "stagger", "root", "ignore_armor", "ignore_chitin", "finisher_scale",
        "damage_multiplier",
    };

    /// <summary>Reaction triggers CombatManager.Reactions.cs resolves (spec §7, M4).</summary>
    public static readonly HashSet<string> KnownReactionTriggers = new(StringComparer.OrdinalIgnoreCase)
    {
        "enemy_move_end",     // Brace: an enemy ends its movement in the zone
        "enemy_enter_zone",   // Set Against Charge: an enemy steps into the zone; its movement ends there
    };

    private static Dictionary<string, ManeuverDefinition> _byId;
    private static Dictionary<WeaponClass, List<ManeuverDefinition>> _byClass;

    public static IReadOnlyDictionary<string, ManeuverDefinition> All
    {
        get { EnsureLoaded(); return _byId; }
    }

    public static ManeuverDefinition Get(string id)
    {
        EnsureLoaded();
        if (!string.IsNullOrEmpty(id) && _byId.TryGetValue(id, out var m))
            return m;
        GD.PrintErr($"ManeuverRegistry: Unknown maneuver '{id}'");
        return null;
    }

    /// <summary>The maneuvers a weapon class grants, in file order. Empty for
    /// None and for classes with no authored maneuvers yet (post-launch set).</summary>
    public static List<ManeuverDefinition> ForWeaponClass(WeaponClass cls)
    {
        EnsureLoaded();
        return _byClass.TryGetValue(cls, out var list) ? list : new List<ManeuverDefinition>();
    }

    /// <summary>Force a reload (debug panel). Returns the number of maneuvers accepted.</summary>
    public static int Reload()
    {
        _byId = null;
        _byClass = null;
        EnsureLoaded();
        return _byId.Count;
    }

    private static void EnsureLoaded()
    {
        if (_byId != null)
            return;

        _byId = new Dictionary<string, ManeuverDefinition>();
        _byClass = new Dictionary<WeaponClass, List<ManeuverDefinition>>();

        var dir = DirAccess.Open(MANEUVERS_DIR);
        if (dir == null)
        {
            GD.PushError($"ManeuverRegistry: cannot open {MANEUVERS_DIR}");
            return;
        }

        var files = new List<string>();
        dir.ListDirBegin();
        for (string f = dir.GetNext(); !string.IsNullOrEmpty(f); f = dir.GetNext())
        {
            if (!dir.CurrentIsDir() && f.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                files.Add(f);
        }
        dir.ListDirEnd();
        files.Sort(StringComparer.Ordinal);

        int rejected = 0;
        foreach (var f in files)
        {
            string path = MANEUVERS_DIR + f;
            using var fa = FileAccess.Open(path, FileAccess.ModeFlags.Read);
            if (fa == null)
            {
                GD.PushError($"ManeuverRegistry: cannot read {path}");
                rejected++;
                continue;
            }

            ManeuverDefinition def;
            try
            {
                def = JsonSerializer.Deserialize<ManeuverDefinition>(fa.GetAsText(), JsonOptions);
            }
            catch (Exception ex)
            {
                GD.PushError($"ManeuverRegistry: {f}: {ex.Message}");
                rejected++;
                continue;
            }

            string why = Validate(def, f);
            if (why != null)
            {
                GD.PushError($"ManeuverRegistry: {f} REJECTED: {why}");
                rejected++;
                continue;
            }

            _byId[def.Id] = def;
            var cls = def.WeaponClassValue;
            if (!_byClass.TryGetValue(cls, out var list))
                _byClass[cls] = list = new List<ManeuverDefinition>();
            list.Add(def);
        }

        GD.Print($"[ManeuverRegistry] {_byId.Count} maneuver(s) loaded, {rejected} rejected.");
    }

    /// <summary>Null when the definition is sound; otherwise the reason it is refused.</summary>
    public static string Validate(ManeuverDefinition def, string file = "")
    {
        if (def == null)
            return "empty definition";
        if (string.IsNullOrWhiteSpace(def.Id))
            return "missing id";
        if (_byId != null && _byId.ContainsKey(def.Id))
            return $"duplicate id '{def.Id}'";
        if (string.IsNullOrWhiteSpace(def.DisplayName))
            return $"'{def.Id}': missing displayName";

        if (!Enum.TryParse<WeaponClass>(def.WeaponClass, ignoreCase: true, out var cls) || cls == WeaponClass.None)
            return $"'{def.Id}': weaponClass '{def.WeaponClass}' is not a classed weapon";
        if (!Enum.TryParse<ManeuverShape>(def.Shape, ignoreCase: true, out var shape))
            return $"'{def.Id}': shape '{def.Shape}' unknown (Self, Adjacent, Range, Line)";
        if ((shape == ManeuverShape.Range || shape == ManeuverShape.Line) && def.Reach < 1)
            return $"'{def.Id}': {shape} needs reach >= 1";

        if (def.ApCost < 0)
            return $"'{def.Id}': apCost below 0";
        if (def.DrainAll)
        {
            if (def.MinEdge < 1 || def.MinEdge > EdgeRules.CapAtTrainingTier3)
                return $"'{def.Id}': drainAll needs minEdge in 1..{EdgeRules.CapAtTrainingTier3}";
        }
        else if (def.EdgeCost < 0 || def.EdgeCost > EdgeRules.BaseCap)
            return $"'{def.Id}': edgeCost {def.EdgeCost} outside 0..{EdgeRules.BaseCap} (spec §3: 1 to 3 at launch)";

        if (def.IsReaction)
        {
            if (shape != ManeuverShape.Self)
                return $"'{def.Id}': a reaction arms on Self; its zone comes from reach";
            if (!KnownReactionTriggers.Contains(def.ReactionTrigger ?? ""))
                return $"'{def.Id}': reactionTrigger '{def.ReactionTrigger}' has no resolver";
            if (def.DrainAll)
                return $"'{def.Id}': a reaction cannot be a finisher";
        }
        else if (!string.IsNullOrEmpty(def.ReactionTrigger))
            return $"'{def.Id}': reactionTrigger set on a non-reaction";

        if (def.Effects == null || def.Effects.Count == 0)
            return $"'{def.Id}': no effects (authoring rule: a maneuver changes where, when or whom)";
        foreach (var e in def.Effects)
        {
            if (e == null || string.IsNullOrWhiteSpace(e.Key))
                return $"'{def.Id}': empty effect key";
            if (!KnownEffectKeys.Contains(e.Key))
                return $"'{def.Id}': effect key '{e.Key}' has no resolver";
            if (e.Key.Equals("push", StringComparison.OrdinalIgnoreCase) && e.Value < 1)
                return $"'{def.Id}': push needs value >= 1";
            if (e.Key.Equals("root", StringComparison.OrdinalIgnoreCase) && e.Value < 1)
                return $"'{def.Id}': root needs value >= 1 (turns)";
            if (e.Key.Equals("finisher_scale", StringComparison.OrdinalIgnoreCase) && !def.DrainAll)
                return $"'{def.Id}': finisher_scale needs drainAll";
            if (e.Key.Equals("damage_multiplier", StringComparison.OrdinalIgnoreCase) && e.Value < 2)
                return $"'{def.Id}': damage_multiplier needs value >= 2";
        }
        if (def.HasEffect("stagger") && shape == ManeuverShape.Range && def.Reach > 1
            && cls != WeaponClass.Sling)
            return $"'{def.Id}': ranged stagger is sling-only (R7)";
        return null;
    }
}
