using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// CombatDebugLauncher.Patterns.cs  (partial of CombatDebugLauncher)
//
// Purpose:   Two debug-launcher helpers split out to keep the main
//            file readable:
//            1) the expedition-pattern picker — spawn a premade enemy
//               composition straight from a region's encounterPools;
//            2) the archetype-grouped enemy roster builder.
// Collaborators: EncounterPoolLoader / RegionLoader (pattern source),
//            UnitRegistry (token -> unit id + BehaviorKey), the main
//            CombatDebugLauncher partial (_enemySpins, _tierOpt, _status,
//            AddSpin, AddStringDropdown, DefaultCount).
// ============================================================
public partial class CombatDebugLauncher : CanvasLayer
{
    private OptionButton _patternRegionOpt;
    private OptionButton _patternOpt;
    private readonly List<RegionDefinition> _regions = new();
    private readonly List<(CompositionData comp, EncounterTier tier)> _patterns = new();

    // E3 map-object test picker: a spinbox per catalog kind -> PlayerSession.DebugMapObjects.
    private readonly Dictionary<string, SpinBox> _mapObjectSpins = new();
    private static readonly string[] MapObjectKinds =
    {
        "cracked_pillar", "resonant_crystal", "ember_brazier",
        "boulder", "ward_stone", "powder_cask",
    };

    // E6: force a specific battlefield archetype (overrides terrain -> recipe).
    private OptionButton _forceRecipeOpt;
    private static readonly string[] BattlefieldRecipes =
    {
        "bf_causeway", "bf_cauldron", "bf_terraces", "bf_warren", "bf_courtyard",
        "bf_spine", "bf_ford", "bf_kiln", "bf_grove", "bf_amphitheater",
    };

    // -- Expedition-pattern picker -----------------------------------------

    /// <summary>Repopulate the pattern dropdown from region[regionIdx]'s encounterPools,
    /// listing every composition across all four tiers. _patterns is kept parallel to the
    /// dropdown so ApplySelectedPattern can resolve the choice back to a composition.</summary>
    private void RebuildPatternDropdown(int regionIdx)
    {
        _patterns.Clear();
        _patternOpt.Clear();
        if (regionIdx >= 0 && regionIdx < _regions.Count)
        {
            var pool = EncounterPoolLoader.Load(_regions[regionIdx].Id);
            if (pool != null)
            {
                AddPatternGroup(pool.Skirmish, EncounterTier.Skirmish);
                AddPatternGroup(pool.Battle, EncounterTier.Battle);
                AddPatternGroup(pool.Siege, EncounterTier.Siege);
                AddPatternGroup(pool.Ambush, EncounterTier.Ambush);
            }
        }
        if (_patterns.Count == 0)
            _patternOpt.AddItem("(no patterns in this region)", 0);
        _patternOpt.Selected = 0;
    }

    private void AddPatternGroup(List<CompositionData> comps, EncounterTier tier)
    {
        if (comps == null)
            return;
        foreach (var c in comps)
        {
            if (c == null)
                continue;
            int n = c.Enemies?.Count ?? 0;
            _patternOpt.AddItem($"{tier}: {c.Name} ({n})", _patterns.Count);
            _patterns.Add((c, tier));
        }
    }

    /// <summary>Clear the roster and fill it from the selected composition, resolving each
    /// archetype token to a unit id the same way EncounterPoolLoader does. Also snaps the
    /// Tier dropdown to the pattern's tier. Objective/waves on the pattern are NOT applied
    /// automatically -- use the manual checkboxes for those.</summary>
    private void ApplySelectedPattern()
    {
        int idx = _patternOpt.Selected;
        if (idx < 0 || idx >= _patterns.Count)
        {
            _status.Text = "No pattern selected.";
            _status.AddThemeColorOverride("font_color", UITheme.Danger);
            return;
        }
        var (comp, tier) = _patterns[idx];
        foreach (var kvp in _enemySpins)
            kvp.Value.Value = 0;

        int placed = 0;
        var unresolved = new List<string>();
        foreach (var slot in comp.Enemies)
        {
            if (UnitRegistry.TryResolveId(slot.Archetype, out var unitId)
                && _enemySpins.TryGetValue(unitId, out var spin))
            {
                spin.Value += 1;
                placed++;
            }
            else
            {
                unresolved.Add(slot.Archetype);
            }
        }

        for (int i = 0; i < _tierOpt.ItemCount; i++)
            if (_tierOpt.GetItemId(i) == (int)tier) { _tierOpt.Selected = i; break; }

        string extra = "";
        if (comp.Objective != null || (comp.Waves != null && comp.Waves.Count > 0))
            extra += " (pattern carries objective/waves -- set those via the checkboxes)";
        if (unresolved.Count > 0)
            extra += $" [unresolved: {string.Join(", ", unresolved)}]";
        _status.Text = placed > 0
            ? $"Pattern '{comp.Name}' ({tier}): {placed} enemy(ies) placed.{extra}"
            : $"Pattern '{comp.Name}': nothing placed.{extra}";
        _status.AddThemeColorOverride("font_color", placed > 0 ? UITheme.TextSecondary : UITheme.Danger);
    }

    // -- Enemy roster (grouped by archetype) -------------------------------

    /// <summary>Archetype groups for the debug roster, in display order. Key = a unit's
    /// BehaviorKey; Label = the section header. Units whose key isn't listed fall into a
    /// trailing "Other / unkeyed" group.</summary>
    private static readonly (string key, string label)[] ArchetypeGroups =
    {
        ("melee_advance",           "Melee - Advancers"),
        ("melee_target_highest_hp", "Melee - Threat-seekers"),
        ("melee_hunt_wounded",      "Melee - Hunters"),
        ("hold_until_near",         "Melee - Defenders"),
        ("ranged_kite",             "Ranged - Kiters"),
        ("ranged_charge",           "Ranged - Artillery"),
        ("shove",                   "Shovers"),
    };

    /// <summary>Emit the enemy count spinboxes grouped by AI archetype (BehaviorKey),
    /// alphabetical by threat label within each group, each group under its own header.</summary>
    private void BuildEnemyRoster(VBoxContainer form)
    {
        var known = new HashSet<string>();
        foreach (var g in ArchetypeGroups)
            known.Add(g.key);

        var byKey = new Dictionary<string, List<string>>();
        var other = new List<string>();
        foreach (string id in UnitRegistry.AllIds)
        {
            string key = UnitRegistry.Get(id)?.BehaviorKey ?? "";
            if (known.Contains(key))
            {
                if (!byKey.TryGetValue(key, out var list)) { list = new List<string>(); byKey[key] = list; }
                list.Add(id);
            }
            else
            {
                other.Add(id);
            }
        }

        bool first = true;
        foreach (var g in ArchetypeGroups)
            if (byKey.TryGetValue(g.key, out var ids) && ids.Count > 0)
            {
                EmitArchetypeGroup(form, g.label, ids, leadingSeparator: !first);
                first = false;
            }
        if (other.Count > 0)
            EmitArchetypeGroup(form, "Other / unkeyed", other, leadingSeparator: !first);
    }

    private void EmitArchetypeGroup(VBoxContainer form, string header, List<string> ids, bool leadingSeparator)
    {
        ids.Sort((a, b) =>
        {
            string la = UnitRegistry.Get(a)?.ThreatLabel ?? a;
            string lb = UnitRegistry.Get(b)?.ThreatLabel ?? b;
            int c = string.Compare(la, lb, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        });
        // Group header — a rule above (except the first group) plus a gold, larger,
        // upper-cased label makes the archetype boundaries easy to scan past.
        if (leadingSeparator)
            form.AddChild(new HSeparator());
        var head = new Label { Text = header.ToUpperInvariant() };
        head.AddThemeFontSizeOverride("font_size", 15);
        head.AddThemeColorOverride("font_color", UITheme.Gold);
        form.AddChild(head);
        foreach (string id in ids)
        {
            var def = UnitRegistry.Get(id);
            string tags = def.BehaviorTags.Count > 0 ? $" [{string.Join(",", def.BehaviorTags)}]" : "";
            _enemySpins[id] = AddSpin(form, $"  {def.ThreatLabel}{tags}:", 0, 8, 1, DefaultCount(id));
        }
    }
}
