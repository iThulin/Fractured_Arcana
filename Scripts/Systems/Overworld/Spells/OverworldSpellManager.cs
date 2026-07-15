using Godot;
using System.Collections.Generic;

// ============================================================
// OverworldSpellManager.cs  (S1+S2, 2026-07-15)
//
// Purpose:        The overworld casting engine for one expedition:
//                 castable-list resolution (school innates +
//                 prepared spells), the casting state machine
//                 (Idle → Targeting → resolve), Essence accounting
//                 with the corrupted-ground surcharge, once-per-
//                 expedition caps, and the EffectKey dispatcher —
//                 bespoke key per spell, the enemy-archetype
//                 dispatcher shape. World mutation lives in
//                 ExpeditionManager's Spell* façade; timed effect
//                 windows live in OverworldSpellEffects; this node
//                 owns the decisions, not the world.
//
//                 Attunements: applied as passive fog effects
//                 after each party move (ApplyAttunement).
//                 Silhouettes they create become Charted through
//                 the standard WriteVisibleToWorld pass — an
//                 attunement charts terrain along the route, per
//                 G2 (terrain shape, never contents).
//
//                 S2 scope: TargetingType None + Tile. Path and
//                 PatrolToken targeting, companion-granted casting
//                 (+1 off-caster tax), scrolls, and echo emission
//                 land in S3–S5. Unknown EffectKeys render greyed
//                 out — data may lead implementation safely.
// Layer:          System
// Collaborators:  ExpeditionManager.cs (façade + lifecycle),
//                 OverworldSpellRegistry.cs (definitions),
//                 OverworldSpellEffects.cs (timed windows),
//                 GrimoireState (save data), GrimoirePanel.cs (UI)
// See:            overworld_spell_system_v1_1.docx §4–§7, §12–§13
// ============================================================

/// <summary>Casting engine for overworld spells. One per expedition scene,
/// child of ExpeditionManager. See header for scope.</summary>
public partial class OverworldSpellManager : Node2D
{
    /// <summary>Base per-expedition Essence pool (§5 Group D starting value).</summary>
    public const int BaseEssencePool = 10;

    private enum CastState { Idle, Targeting, PathTargeting }

    private ExpeditionManager _expedition;
    private OverworldHexGrid _grid;
    private GrimoireState _grimoire;
    private string _school = "";

    private CastState _state = CastState.Idle;
    private OverworldSpellDefinition _targetingSpell;
    private readonly List<Vector2I> _validTargets = new();
    private readonly List<OverworldHex> _highlighted = new();

    // ── S3: path targeting (Bone Scout, Beast Envoy) ─────────────────────
    private readonly List<Vector2I> _path = new();
    private int _pathMax;

    /// <summary>S3 (Emulate): cost override consumed at the next resolve.</summary>
    private int? _costOverride = null;

    /// <summary>S3 (Minor Working): the option picked from the popup.</summary>
    private int _minorChoice = 0;
    private PopupMenu _minorMenu;

    /// <summary>EffectKeys the S2 dispatcher implements. A definition outside
    /// this set is authored-but-not-yet-built: shown greyed out.</summary>
    private static readonly HashSet<string> ImplementedKeys = new()
    {
        // S2
        "force_path", "tremorsense", "scrying_lens", "verdant_passage",
        "ember_ward", "ley_tap",
        "mending_cant", "purifying_rite", "wayfarers_beacon", "campward",
        // S3
        "retrace", "auspice", "stasis_snare",
        "veil", "parley_compulsion", "beguile",
        "thornwall", "fulminant_charge", "deploy_waystation", "clockwork_skimmer",
        "speak_fallen", "bone_scout", "beast_envoy", "pallid_bargain",
        "minor_working", "emulate", "attuned_recall",
    };

    /// <summary>Attunement keys applied. All eight schools as of S3.</summary>
    private static readonly HashSet<string> ImplementedAttunements = new()
    {
        "elemental_sense", "wildsense", "deathsight",     // fog effects
        "foreboding", "true_names",                        // token-vision flags
        "surveyors_eye", "arcane_literacy",                // tooltip extras
        "versatility",                                     // tax waiver (+slot, S4 UI)
    };
    private bool _warnedUnbuiltAttunement = false;

    private bool IsAdept => _school == "Adept";

    public bool IsTargeting => _state == CastState.Targeting;

    // ════════════════════════════════════════════════════════════════════
    // Lifecycle
    // ════════════════════════════════════════════════════════════════════

    /// <summary>Wire references and (on a FRESH deploy only) reset the
    /// expedition-scoped spell state. Combat/negotiation returns keep the
    /// pool, cast counts, and beacons — they ride the save.</summary>
    public void Initialize(ExpeditionManager expedition, OverworldHexGrid grid,
                           GrimoireState grimoire, bool freshDeploy)
    {
        _expedition = expedition;
        _grid = grid;
        _grimoire = grimoire;
        _school = SaveManager.ActiveSave?.Cycle?.SelectedSchool ?? "";

        GrimoireState.AssertRoundTripOnce();
        OverworldSpellRegistry.EnsureLoaded();

        if (freshDeploy)
        {
            _grimoire.BeginExpedition(BaseEssencePool);
            OverworldSpellEffects.Clear();
        }

        // Redraw persisted beacons (survive combat round-trips + saves).
        foreach (var mark in _grimoire.ActiveBeacons)
        {
            var parts = mark.Split(',');
            if (parts.Length == 2 &&
                int.TryParse(parts[0], out int col) && int.TryParse(parts[1], out int row))
                _expedition.SpellDrawBeaconMarker(_expedition.WindowRef.LocalOf(col, row));
        }

        // S3: redraw waystations, and remnants when someone can use them.
        foreach (var mark in _grimoire.ActiveWaystations)
        {
            var parts = mark.Split(',');
            if (parts.Length == 2 &&
                int.TryParse(parts[0], out int col) && int.TryParse(parts[1], out int row))
                _expedition.SpellDrawWaystationMarker(
                    _expedition.WindowRef.LocalOf(col, row), col, row);
        }
        if (HasNecromancerAccess())
            foreach (var mark in _grimoire.ActiveRemnants)
            {
                var parts = mark.Split(',');
                if (parts.Length == 2 &&
                    int.TryParse(parts[0], out int col) && int.TryParse(parts[1], out int row))
                    _expedition.SpellDrawRemnantMarker(_expedition.WindowRef.LocalOf(col, row));
            }

        // S3: attunement vision flags — set once per scene build (Clear() on a
        // fresh deploy has already run above, so these survive it).
        var att = OverworldSpellRegistry.AttunementFor(_school);
        OverworldSpellEffects.ForebodingVision = att?.EffectKey == "foreboding";
        OverworldSpellEffects.TrueNamesVision = att?.EffectKey == "true_names";

        GD.Print($"[Grimoire] School={_school}, Essence {_grimoire.EssenceCurrent}/{_grimoire.EssenceMax}, " +
                 $"known={_grimoire.KnownSpellIds.Count}, prepared={_grimoire.PreparedSpellIds.Count}" +
                 $"{(freshDeploy ? " (fresh pool)" : " (restored)")}.");
    }

    // ════════════════════════════════════════════════════════════════════
    // Castable list + gating
    // ════════════════════════════════════════════════════════════════════

    /// <summary>The spells this expedition can cast right now: the school's
    /// innates (always prepared, no slots) + the prepared loadout. DebugMode
    /// additionally exposes every implemented spell in the registry — the S1
    /// "debug-cast from console" path, panel edition.</summary>
    public List<OverworldSpellDefinition> CastableSpells()
    {
        var result = new List<OverworldSpellDefinition>();
        var seen = new HashSet<string>();

        void Add(OverworldSpellDefinition d)
        {
            if (d != null && !d.IsAttunement && seen.Add(d.Id))
                result.Add(d);
        }

        foreach (var innate in OverworldSpellRegistry.InnatesFor(_school))
            Add(innate);
        foreach (var id in _grimoire.PreparedSpellIds)
            Add(OverworldSpellRegistry.Get(id));

        // S3 (§4a): active-party companions of another school grant that
        // school's innates at +1 Essence (off-caster tax; waived for the
        // Adept). They occupy no slots and leave when the companion leaves —
        // losing your only Enchanter mid-expedition removes Veil. Intended.
        foreach (string school in ActiveCompanionSchools())
            if (school != _school)
                foreach (var innate in OverworldSpellRegistry.InnatesFor(school))
                    Add(innate);

        if (PlayerSession.DebugMode)
            foreach (var d in OverworldSpellRegistry.All.Values)
                if (ImplementedKeys.Contains(d.EffectKey))
                    Add(d);

        return result;
    }

    /// <summary>Full Essence cost right now: base + off-caster tax (+1 for a
    /// non-General spell outside the wizard's school — companion-granted or
    /// debug; waived for the Adept, §7h) + corrupted-ground surcharge
    /// (casting FROM a tile of corruption tier T costs +T, §5).</summary>
    public int CastCostOf(OverworldSpellDefinition def)
        => def.EssenceCost + OffCasterTax(def) + CorruptionSurcharge();

    /// <summary>S3: the off-caster tax for one spell (0 or 1).</summary>
    public int OffCasterTax(OverworldSpellDefinition def)
        => (def.School != "General" && def.School != _school && !IsAdept) ? 1 : 0;

    public int CorruptionSurcharge() => _expedition?.SpellCorruptionTierAtParty() ?? 0;

    /// <summary>Schools of usable active-party companions.</summary>
    private List<string> ActiveCompanionSchools()
    {
        var result = new List<string>();
        var save = SaveManager.ActiveSave;
        if (save == null)
            return result;
        foreach (var id in save.ActivePartyCompanionIds)
        {
            var c = save.Companions.Find(x => x.Id == id && x.IsRecruited &&
                                              !x.IsPermadead && !x.IsInjured);
            if (c != null && !string.IsNullOrEmpty(c.School) && !result.Contains(c.School))
                result.Add(c.School);
        }
        return result;
    }

    /// <summary>Necromancer spells need Remnants; markers draw when anyone
    /// aboard can cast from them.</summary>
    private bool HasNecromancerAccess()
        => _school == "Necromancer" ||
           ActiveCompanionSchools().Contains("Necromancer") ||
           PlayerSession.DebugMode;

    /// <summary>Party stands on or beside a Remnant (world-coord marks).</summary>
    private bool NearRemnant()
    {
        if (_grimoire.ActiveRemnants.Count == 0)
            return false;
        if (!_expedition.WindowRef.TryLocalToWorld(_expedition.PartyLocal, out int pc, out int pr))
            return false;
        foreach (var mark in _grimoire.ActiveRemnants)
        {
            var parts = mark.Split(',');
            if (parts.Length == 2 &&
                int.TryParse(parts[0], out int c) && int.TryParse(parts[1], out int r) &&
                _expedition.WorldRef.HexDistance(pc, pr, c, r) <= 1)
                return true;
        }
        return false;
    }

    private bool OnRuins()
        => _grid.Hexes.TryGetValue(_expedition.PartyLocal, out var h) &&
           h.Terrain == OverworldHex.TerrainType.Ruins;

    /// <summary>Null when castable; otherwise the human-readable reason the
    /// Grimoire panel shows (and disables the button with).</summary>
    public string CastBlockReason(OverworldSpellDefinition def)
    {
        if (!ImplementedKeys.Contains(def.EffectKey))
            return "not yet implemented";
        if (def.OncePerExpedition &&
            _grimoire.PerExpeditionCastCounts.TryGetValue(def.Id, out int n) && n > 0)
            return "already cast this expedition";
        if (CastCostOf(def) > _grimoire.EssenceCurrent)
            return "not enough Essence";

        // S3: contextual requirements, surfaced before the button is pressed (G5).
        switch (def.EffectKey)
        {
            case "retrace" when !_expedition.CanRetrace:
                return "no step to retrace";
            case "bone_scout" when !NearRemnant():
                return "no Remnant within reach";
            case "speak_fallen" when !NearRemnant() && !OnRuins():
                return "no Remnant or ruin here";
            case "parley_compulsion" when _grimoire.ParleyArmed:
                return "already armed";
            case "beguile" when _grimoire.BeguileArmed:
                return "already armed";
            case "campward" when OverworldSpellEffects.CampwardArmed:
                return "already armed";
            case "pallid_bargain" when _expedition.CurrentHP <= (int)def.Param("hp", 4):
                return "not enough blood to bargain";
            case "emulate" when EmulateTarget() == null:
                return "nothing to emulate";
        }
        return null;
    }

    /// <summary>S3 (Emulate): the recastable last spell, or null.</summary>
    private OverworldSpellDefinition EmulateTarget()
    {
        var last = OverworldSpellRegistry.Get(_grimoire.LastCastSpellId);
        if (last == null || last.EffectKey == "emulate" ||
            !ImplementedKeys.Contains(last.EffectKey))
            return null;
        return last;
    }

    // ════════════════════════════════════════════════════════════════════
    // Casting state machine
    // ════════════════════════════════════════════════════════════════════

    /// <summary>Panel entry point: begin targeting (Tile spells) or resolve
    /// immediately (None spells).</summary>
    public void RequestCast(string spellId)
    {
        if (_expedition == null || _expedition.ExpeditionComplete)
            return;
        var def = OverworldSpellRegistry.Get(spellId);
        if (def == null)
            return;

        string block = CastBlockReason(def);
        if (block != null)
        {
            _expedition.SpellInfo($"{def.Name}: {block}.");
            return;
        }

        CancelTargeting(null); // one targeting session at a time

        // S3 (Emulate): recast the last spell at its cost +1 — inherits the
        // original's flow (targeting and all), Magnitude, and echo profile.
        // Once-per-expedition caps still bind the recast (Retrace stays hard).
        if (def.EffectKey == "emulate")
        {
            var target = EmulateTarget();
            if (target == null)
            { _expedition.SpellInfo("Emulate: nothing to emulate."); return; }
            string tBlock = CastBlockReason(target);
            if (tBlock != null && tBlock != "not enough Essence")
            { _expedition.SpellInfo($"Emulate → {target.Name}: {tBlock}."); return; }
            int cost = target.EssenceCost + 1 + CorruptionSurcharge();
            if (cost > _grimoire.EssenceCurrent)
            { _expedition.SpellInfo("Emulate: not enough Essence."); return; }
            _costOverride = cost;
            RouteCast(target);
            return;
        }

        // S3 (Minor Working): a popup picks the working before anything casts.
        if (def.EffectKey == "minor_working")
        {
            ShowMinorWorkingMenu(def);
            return;
        }

        RouteCast(def);
    }

    /// <summary>Send a validated cast down its targeting flow.</summary>
    private void RouteCast(OverworldSpellDefinition def)
    {
        if (def.TargetingType == "Tile")
            BeginTargeting(def);
        else if (def.TargetingType == "Path")
            BeginPathTargeting(def);
        else
            ResolveCast(def, _expedition.PartyLocal);
    }

    /// <summary>S3 (Minor Working, Adept): heal · chart · ward — never the
    /// best tool, always a tool. The chart option enters tile targeting.</summary>
    private void ShowMinorWorkingMenu(OverworldSpellDefinition def)
    {
        if (_minorMenu == null)
        {
            _minorMenu = new PopupMenu { Name = "MinorWorkingMenu" };
            _minorMenu.AddItem("Mend — heal 3 party HP", 0);
            _minorMenu.AddItem("Glimpse — chart 1 adjacent unseen hex", 1);
            _minorMenu.AddItem("Ward — no terrain drain for 3 steps", 2);
            AddChild(_minorMenu);
            _minorMenu.IdPressed += id =>
            {
                var mw = OverworldSpellRegistry.Get("minor_working");
                if (mw == null || CastBlockReason(mw) != null)
                    return;
                _minorChoice = (int)id;
                if (_minorChoice == 1)
                    BeginTargeting(mw);      // adjacent Unseen hex
                else
                    ResolveCast(mw, _expedition.PartyLocal);
            };
        }
        _minorMenu.PopupCentered();
    }

    private void BeginTargeting(OverworldSpellDefinition def)
    {
        _validTargets.Clear();
        CollectValidTargets(def, _validTargets);
        if (_validTargets.Count == 0)
        {
            _costOverride = null; // an Emulate that found no target charges nothing
            _expedition.SpellInfo($"{def.Name}: no valid target in range.");
            return;
        }

        _targetingSpell = def;
        _state = CastState.Targeting;

        foreach (var coord in _validTargets)
        {
            if (!_grid.Hexes.TryGetValue(coord, out var hex))
                continue;
            var highlight = new Polygon2D
            {
                Polygon = OverworldHex.MakeHexPoints(OverworldHex.GetHexSize()),
                Color = UITheme.SpellTargetHighlight,
                ZIndex = 4,
                Name = "SpellTargetHighlight",
            };
            hex.AddChild(highlight);
            _highlighted.Add(hex);
        }

        _expedition.SpellInfo($"{def.Name}: choose a target (right-click or Esc to cancel).");
    }

    /// <summary>Per-EffectKey target validity. Targets are LOADED hexes only —
    /// every S2 range (≤6) sits far inside the loaded window (R=12).</summary>
    private void CollectValidTargets(OverworldSpellDefinition def, List<Vector2I> into)
    {
        var party = _expedition.PartyLocal;
        var world = _expedition.WorldRef;
        var window = _expedition.WindowRef;

        foreach (var kvp in _grid.Hexes)
        {
            var coord = kvp.Key;
            int dist = _grid.Distance(party, coord);
            if (def.Range > 0 && dist > def.Range)
                continue;

            switch (def.EffectKey)
            {
                case "force_path":
                    // Adjacent impassable ground (rockfall/water) — never the
                    // party's own tile.
                    if (dist == 1 &&
                        (kvp.Value.IsWater || kvp.Value.Terrain == OverworldHex.TerrainType.Mountain))
                        into.Add(coord);
                    break;

                case "scrying_lens":
                    // Any already-Charted (or Explored) tile within range —
                    // the leapfrog anchor (G2: charts, never explores).
                    if (window.TryLocalToWorld(coord, out int c, out int r) &&
                        world.GetTile(c, r).Discovery != TileDiscovery.Unseen)
                        into.Add(coord);
                    break;

                case "clockwork_skimmer":
                    // Fire-and-forget: any loaded tile in range, no anchor
                    // requirement — pays for the freedom in reach.
                    into.Add(coord);
                    break;

                case "thornwall":
                    // Adjacent land the wall can take root in.
                    if (dist == 1 && kvp.Value.IsLand)
                        into.Add(coord);
                    break;

                case "stasis_snare":
                    // A visible patrol's tile.
                    if (_expedition.VisiblePatrolCoords().Contains(coord))
                        into.Add(coord);
                    break;

                case "minor_working":
                    // Glimpse: an adjacent world-Unseen hex.
                    if (dist == 1 &&
                        window.TryLocalToWorld(coord, out int mc, out int mr) &&
                        world.GetTile(mc, mr).Discovery == TileDiscovery.Unseen)
                        into.Add(coord);
                    break;
            }
        }
    }

    // ── S3: path targeting (Bone Scout, Beast Envoy) ──────────────────────

    /// <summary>Begin drawing a scout path from the party's tile: click a
    /// hex adjacent to the path's end to extend, click the end again to send
    /// the scout, Esc/right-click to cancel. Live count in the info line.</summary>
    private void BeginPathTargeting(OverworldSpellDefinition def)
    {
        _targetingSpell = def;
        _pathMax = def.Range > 0 ? def.Range : 5;
        _path.Clear();
        _path.Add(_expedition.PartyLocal);
        _state = CastState.PathTargeting;
        RefreshPathHighlights();
        _expedition.SpellInfo($"{def.Name}: draw a path (0/{_pathMax}) — click the last hex to send, right-click to cancel.");
    }

    private void RefreshPathHighlights()
    {
        foreach (var hex in _highlighted)
        {
            hex.GetNodeOrNull("SpellTargetHighlight")?.QueueFree();
            hex.GetNodeOrNull("SpellPathHighlight")?.QueueFree();
        }
        _highlighted.Clear();

        // The drawn path (solid) …
        for (int i = 1; i < _path.Count; i++)
            if (_grid.Hexes.TryGetValue(_path[i], out var hex))
            {
                hex.AddChild(new Polygon2D
                {
                    Polygon = OverworldHex.MakeHexPoints(OverworldHex.GetHexSize()),
                    Color = new Color(UITheme.SpellTargetHighlight, 0.55f),
                    ZIndex = 4,
                    Name = "SpellPathHighlight",
                });
                _highlighted.Add(hex);
            }

        // … and, if it can still grow, the candidate next steps (faint).
        if (_path.Count - 1 < _pathMax)
            foreach (var n in _grid.GetNeighbors(_path[^1]))
            {
                if (_path.Contains(n) || !_grid.Hexes.TryGetValue(n, out var hex))
                    continue;
                hex.AddChild(new Polygon2D
                {
                    Polygon = OverworldHex.MakeHexPoints(OverworldHex.GetHexSize()),
                    Color = UITheme.SpellTargetHighlight,
                    ZIndex = 4,
                    Name = "SpellTargetHighlight",
                });
                _highlighted.Add(hex);
            }
    }

    private bool HandlePathClick(Vector2I axial)
    {
        // Click the path's end again → send the scout.
        if (axial == _path[^1] && _path.Count > 1)
        {
            var def = _targetingSpell;
            var path = new List<Vector2I>(_path);
            CancelTargeting(null);
            ResolveCast(def, path[^1], path);
            return true;
        }

        // Extend: adjacent to the end, unvisited, loaded, within length.
        if (_path.Count - 1 < _pathMax &&
            !_path.Contains(axial) &&
            _grid.Hexes.ContainsKey(axial) &&
            _grid.GetNeighbors(_path[^1]).Contains(axial))
        {
            _path.Add(axial);
            RefreshPathHighlights();
            _expedition.SpellInfo($"{_targetingSpell.Name}: path {_path.Count - 1}/{_pathMax} — " +
                                  "click the last hex to send, right-click to cancel.");
            return true;
        }

        _expedition.SpellInfo("The scout can't reach that — extend from the path's end.");
        return true;
    }

    /// <summary>Grid click while targeting. Returns true when the click was
    /// consumed (valid or not) so ExpeditionManager doesn't route it to
    /// movement; false when Idle.</summary>
    public bool HandleHexClicked(Vector2I axial)
    {
        if (_state == CastState.PathTargeting)
            return HandlePathClick(axial);
        if (_state != CastState.Targeting)
            return false;

        if (_validTargets.Contains(axial))
        {
            var def = _targetingSpell;
            CancelTargeting(null);
            ResolveCast(def, axial);
        }
        else
        {
            _expedition.SpellInfo("Not a valid target. Right-click or Esc to cancel.");
        }
        return true;
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (_state == CastState.Idle)
            return;
        bool cancel = e is InputEventKey { Pressed: true, Keycode: Key.Escape } ||
                      e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right };
        if (cancel)
        {
            CancelTargeting("Cast cancelled.");
            GetViewport().SetInputAsHandled();
        }
    }

    private void CancelTargeting(string message)
    {
        foreach (var hex in _highlighted)
        {
            hex.GetNodeOrNull("SpellTargetHighlight")?.QueueFree();
            hex.GetNodeOrNull("SpellPathHighlight")?.QueueFree();
        }
        _highlighted.Clear();
        _validTargets.Clear();
        _path.Clear();
        _targetingSpell = null;
        _state = CastState.Idle;
        if (message != null)
        {
            _costOverride = null; // an aborted Emulate charges nothing
            _expedition.SpellInfo(message);
        }
    }

    // ════════════════════════════════════════════════════════════════════
    // Resolution + dispatch
    // ════════════════════════════════════════════════════════════════════

    private void ResolveCast(OverworldSpellDefinition def, Vector2I target,
                             List<Vector2I> path = null)
    {
        // Re-validate at resolve — the surcharge can differ from panel time.
        // (Emulate carries a cost override; its Essence gate was checked at
        // request time against that override, so skip the base-cost gate.)
        string block = CastBlockReason(def);
        if (block != null && !(block == "not enough Essence" && _costOverride.HasValue))
        {
            _costOverride = null;
            _expedition.SpellInfo($"{def.Name}: {block}.");
            return;
        }

        int cost = _costOverride ?? CastCostOf(def);
        _costOverride = null;
        if (cost > _grimoire.EssenceCurrent)
        {
            _expedition.SpellInfo($"{def.Name}: not enough Essence.");
            return;
        }

        string result = Dispatch(def, target, path);
        if (result == null)
        {
            // Effect refused (validation raced) — no Essence spent (G5:
            // legible costs; you never pay for nothing).
            _expedition.SpellRefreshHud();
            return;
        }

        _grimoire.EssenceCurrent -= cost;
        _grimoire.PerExpeditionCastCounts[def.Id] =
            _grimoire.PerExpeditionCastCounts.TryGetValue(def.Id, out int n) ? n + 1 : 1;
        _grimoire.LastCastSpellId = def.Id; // S3: Emulate's memory
        SaveManager.MarkDirty();

        int surcharge = cost - def.EssenceCost;
        _expedition.SpellInfo($"{def.Name}: {result} (−{cost} Essence" +
                              $"{(surcharge > 0 ? $", {surcharge} beyond base" : "")}.)");

        // §6a stub: Overt/Grand casts in kingdom territory are witnessed.
        // Real echo emission (SpellcraftAid/Transgression) lands in S5.
        if (def.Magnitude != "Subtle")
        {
            string kid = _expedition.SpellKingdomAtParty();
            if (!string.IsNullOrEmpty(kid))
                GD.Print($"[Spellcraft] {def.Magnitude} cast of '{def.Id}' witnessed in '{kid}' " +
                         "— echo emission lands in S5.");
        }

        _expedition.SpellRefreshHud();
    }

    /// <summary>The dispatcher — bespoke key per spell. Returns a short result
    /// string for the info line, or null if the effect refused (no charge).</summary>
    private string Dispatch(OverworldSpellDefinition def, Vector2I target,
                            List<Vector2I> path = null)
    {
        var world = _expedition.WorldRef;
        var window = _expedition.WindowRef;

        switch (def.EffectKey)
        {
            // ── S3 ────────────────────────────────────────────────────────
            case "retrace":
                return _expedition.SpellRetrace()
                    ? "the last step unhappens — ground and cost restored"
                    : null;

            case "auspice":
            {
                int flagged = _expedition.SpellAuspicePreview();
                return flagged > 0
                    ? $"the next moon shows itself — {flagged} tile(s) marked for the creep"
                    : "the next moon shows itself — the corruption rests near you";
            }

            case "stasis_snare":
                return _expedition.SpellStunPatrolAt(target, (int)def.Param("steps", 6))
                    ? $"the patrol hangs in stilled time for {(int)def.Param("steps", 6)} steps"
                    : null;

            case "veil":
                OverworldSpellEffects.VeilStepsLeft =
                    Mathf.Max(OverworldSpellEffects.VeilStepsLeft, (int)def.Param("steps", 5));
                return $"the party fades from notice for {(int)def.Param("steps", 5)} steps";

            case "parley_compulsion":
                _grimoire.ParleyArmed = true;
                return "the next patrol to find you will talk instead";

            case "beguile":
                _grimoire.BeguileArmed = true;
                return "the next table opens a band more favorable";

            case "thornwall":
                OverworldSpellEffects.AddPatrolBlock(target, (int)def.Param("steps", 8));
                return $"thorns take the ground for {(int)def.Param("steps", 8)} steps";

            case "fulminant_charge":
                OverworldSpellEffects.AddTrap(_expedition.PartyLocal, (int)def.Param("stun", 4));
                return "the charge is set — the first patrol to walk here will regret it";

            case "deploy_waystation":
                return _expedition.SpellDeployWaystation()
                    ? "the waystation unfolds — one rest, and an anchor while it stands"
                    : null;

            case "clockwork_skimmer":
            {
                if (!window.TryLocalToWorld(target, out int kc, out int kr))
                    return null;
                int charted = _expedition.SpellChartHexRadius(kc, kr, (int)def.Param("radius", 2));
                return $"the skimmer lands — {charted} tile(s) charted";
            }

            case "speak_fallen":
            {
                int exposed = _expedition.SpellChartPatrolPositions();
                string bearing = _expedition.SpellNearestUndiscoveredPoiBearing();
                return $"the dead speak — {exposed} patrol(s) betrayed" +
                       (bearing == "" ? "" : $"; {bearing}");
            }

            case "bone_scout":
            case "beast_envoy":
            {
                if (path == null || path.Count < 2)
                    return null;
                int charted = 0;
                for (int i = 1; i < path.Count; i++)
                    if (window.TryLocalToWorld(path[i], out int sc, out int sr))
                        charted += _expedition.SpellChartHexRadius(sc, sr,
                            (int)def.Param("chartRadius", 0));
                string who = def.EffectKey == "bone_scout" ? "the bones walk" : "the beast runs";
                return $"{who} — {charted} tile(s) charted along the path";
            }

            case "pallid_bargain":
            {
                int hp = (int)def.Param("hp", 4);
                _expedition.CurrentHP -= hp;
                AddEssence((int)def.Param("essence", 3), "Pallid Bargain");
                _expedition.SpellRefreshHud();
                return $"{hp} HP given up for {(int)def.Param("essence", 3)} Essence";
            }

            case "minor_working":
                switch (_minorChoice)
                {
                    case 0:
                        _expedition.SpellHealParty((int)def.Param("heal", 3));
                        return $"the party mends {(int)def.Param("heal", 3)} HP";
                    case 1:
                    {
                        if (!window.TryLocalToWorld(target, out int ac, out int ar))
                            return null;
                        _expedition.SpellChartHexRadius(ac, ar, 0);
                        return "a glimpse past the veil — 1 hex charted";
                    }
                    default:
                        OverworldSpellEffects.AddDrainSuppression("Minor Working",
                            null, (int)def.Param("steps", 3));
                        return $"the ground's bite is dulled for {(int)def.Param("steps", 3)} steps";
                }

            case "attuned_recall":
                return _expedition.SpellRecallBearings() is string b && b != ""
                    ? b : null;

            case "force_path":
                return _expedition.SpellForcePath(target)
                    ? "the way is open — rough going, but passable"
                    : null;

            case "tremorsense":
            {
                // W-track re-rule: "the window" → radius 12 of the party
                // (stable under the sliding window).
                var party = _expedition.PartyLocal;
                if (!window.TryLocalToWorld(party, out int pc, out int pr))
                    return null;
                int charted = _expedition.SpellChartHexRadius(pc, pr,
                    (int)def.Param("radius", 12),
                    new List<OverworldHex.TerrainType>
                    { OverworldHex.TerrainType.Mountain, OverworldHex.TerrainType.Hills });
                return $"the earth speaks — {charted} highland tile(s) charted";
            }

            case "scrying_lens":
            {
                if (!window.TryLocalToWorld(target, out int tc, out int tr))
                    return null;
                int charted = _expedition.SpellChartHexRadius(tc, tr, (int)def.Param("radius", 3));
                return $"{charted} tile(s) charted";
            }

            case "verdant_passage":
                OverworldSpellEffects.AddTerrainCostCap("Verdant Passage",
                    new List<OverworldHex.TerrainType>
                    { OverworldHex.TerrainType.Forest, OverworldHex.TerrainType.Swamp },
                    costCap: 1, steps: (int)def.Param("steps", 5));
                return $"the green parts for {(int)def.Param("steps", 5)} steps";

            case "ember_ward":
                OverworldSpellEffects.AddDrainSuppression("Ember Ward",
                    new List<OverworldHex.TerrainType> { OverworldHex.TerrainType.Volcanic },
                    steps: (int)def.Param("steps", 8));
                return $"volcanic ground holds no heat for {(int)def.Param("steps", 8)} steps";

            case "ley_tap":
            {
                // §5 sanctioned conversion: 1 step → 2 Essence, on Arcane
                // Ground only. The inverse is forbidden (G1).
                if (!_grid.Hexes.TryGetValue(_expedition.PartyLocal, out var hx) ||
                    hx.Terrain != OverworldHex.TerrainType.ArcaneGround)
                { _expedition.SpellInfo("Ley Tap: must stand on Arcane Ground."); return null; }
                if (_expedition.StepsRemaining < 1)
                { _expedition.SpellInfo("Ley Tap: no steps left to tap."); return null; }
                _expedition.StepsRemaining -= 1;
                AddEssence(2, "Ley Tap");
                return "1 step drawn down into 2 Essence";
            }

            case "mending_cant":
                _expedition.SpellHealParty((int)def.Param("heal", 5));
                return $"the party mends {(int)def.Param("heal", 5)} HP";

            case "purifying_rite":
                OverworldSpellEffects.AddCorruptionSuppression("Purifying Rite",
                    (int)def.Param("steps", 10));
                return $"corruption held at bay for {(int)def.Param("steps", 10)} steps";

            case "wayfarers_beacon":
            {
                var party = _expedition.PartyLocal;
                if (!window.TryLocalToWorld(party, out int bc, out int br))
                    return null;
                _grimoire.ActiveBeacons.Add($"{bc},{br}");
                _expedition.SpellDrawBeaconMarker(party);
                return "the mark is set";
            }

            case "campward":
                OverworldSpellEffects.CampwardArmed = true;
                return "the next camp will be a good one";
        }
        return null;
    }

    // ════════════════════════════════════════════════════════════════════
    // Attunement (passive)
    // ════════════════════════════════════════════════════════════════════

    /// <summary>Apply the school Attunement around the party. Called on deploy
    /// and after every move, before WriteVisibleToWorld (so attunement
    /// silhouettes chart through the standard pass).</summary>
    public void ApplyAttunement(Vector2I partyLocal)
    {
        var att = OverworldSpellRegistry.AttunementFor(_school);
        if (att == null)
            return;
        if (!ImplementedAttunements.Contains(att.EffectKey))
        {
            if (!_warnedUnbuiltAttunement)
            {
                _warnedUnbuiltAttunement = true;
                GD.Print($"[Grimoire] Attunement '{att.Id}' not built yet (S3) — inert.");
            }
            return;
        }

        switch (att.EffectKey)
        {
            case "elemental_sense":
            {
                // Volcanic / Arcane Ground / water / frozen terrain silhouettes
                // within 3. "Ice" has no overworld terrain yet — Snow stands in.
                int radius = (int)att.Param("radius", 3);
                foreach (var kvp in _grid.Hexes)
                {
                    if (kvp.Value.Fog != OverworldHex.FogState.Hidden)
                        continue;
                    if (_grid.Distance(partyLocal, kvp.Key) > radius)
                        continue;
                    var t = kvp.Value.Terrain;
                    bool sensed = t == OverworldHex.TerrainType.Volcanic ||
                                  t == OverworldHex.TerrainType.ArcaneGround ||
                                  t == OverworldHex.TerrainType.Snow ||
                                  TerrainClass.IsWater(t);
                    if (!sensed)
                        continue;
                    kvp.Value.Fog = OverworldHex.FogState.Silhouette;
                    kvp.Value.RefreshVisuals();
                }
                break;
            }

            case "wildsense":
            {
                // Forest/Swamp silhouette at extended range; Rest sites within
                // 4 are revealed outright (the animals know where water is).
                int silRadius = (int)att.Param("radius", 3);
                int restRadius = (int)att.Param("restRadius", 4);
                foreach (var kvp in _grid.Hexes)
                {
                    int d = _grid.Distance(partyLocal, kvp.Key);
                    var t = kvp.Value.Terrain;
                    if (kvp.Value.Fog == OverworldHex.FogState.Hidden && d <= silRadius &&
                        (t == OverworldHex.TerrainType.Forest ||
                         t == OverworldHex.TerrainType.Swamp ||
                         t == OverworldHex.TerrainType.Marsh))
                    {
                        kvp.Value.Fog = OverworldHex.FogState.Silhouette;
                        kvp.Value.RefreshVisuals();
                    }
                    if (d <= restRadius && kvp.Value.POI == OverworldHex.POIType.Rest &&
                        !kvp.Value.POIConsumed &&
                        kvp.Value.Fog != OverworldHex.FogState.Revealed)
                    {
                        kvp.Value.Fog = OverworldHex.FogState.Revealed;
                        kvp.Value.RefreshVisuals();
                    }
                }
                break;
            }

            case "deathsight":
            {
                // Ruins silhouette through fog within 3. (Grave-marked POIs
                // don't exist yet; consumed-combat sites are usually already
                // revealed — the Remnant markers carry that half.)
                int radius = (int)att.Param("radius", 3);
                foreach (var kvp in _grid.Hexes)
                {
                    if (kvp.Value.Fog != OverworldHex.FogState.Hidden)
                        continue;
                    if (_grid.Distance(partyLocal, kvp.Key) > radius)
                        continue;
                    if (kvp.Value.Terrain != OverworldHex.TerrainType.Ruins)
                        continue;
                    kvp.Value.Fog = OverworldHex.FogState.Silhouette;
                    kvp.Value.RefreshVisuals();
                }
                break;
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════
    // S3: tooltip extras (Surveyor's Eye · Arcane Literacy)
    // ════════════════════════════════════════════════════════════════════

    /// <summary>Does the wizard's own school attunement carry this key?</summary>
    public bool HasAttunement(string effectKey)
        => OverworldSpellRegistry.AttunementFor(_school)?.EffectKey == effectKey;

    /// <summary>Surveyor's Eye (Tinker): silhouetted hexes show their step
    /// cost and hazard flag before entry.</summary>
    public string TooltipSilhouetteExtra(OverworldHex hex)
    {
        if (!HasAttunement("surveyors_eye"))
            return "";
        bool hazardous = hex.Terrain is OverworldHex.TerrainType.Swamp
            or OverworldHex.TerrainType.Marsh
            or OverworldHex.TerrainType.Snow
            or OverworldHex.TerrainType.Volcanic;
        return $"  ·  {OverworldMovementCost.TerrainStep(hex.Terrain)} step(s)" +
               (hazardous ? "  ·  hazardous" : "");
    }

    /// <summary>Arcane Literacy (Arcanist): revealed POIs show their reward
    /// category — the sole sanctioned peek past G2's contents line, and only
    /// once the POI is already revealed.</summary>
    public string TooltipPoiExtra(OverworldHex hex)
    {
        if (!HasAttunement("arcane_literacy"))
            return "";
        string category = hex.POI switch
        {
            OverworldHex.POIType.Combat => "cards · gold",
            OverworldHex.POIType.Rest => "respite",
            OverworldHex.POIType.Narrative => "lore",
            OverworldHex.POIType.Negotiation => "reputation · goods",
            OverworldHex.POIType.Outpost => "staging",
            OverworldHex.POIType.Prison => "a captive",
            _ => "",
        };
        return category == "" ? "" : $" ({category})";
    }

    // ════════════════════════════════════════════════════════════════════
    // Essence
    // ════════════════════════════════════════════════════════════════════

    /// <summary>Add Essence, clamped to the pool max. Regen sources: Rest (+3),
    /// Outpost (full), Arcane Ground (+1/step), Ley Tap.</summary>
    public void AddEssence(int amount, string source)
    {
        if (_grimoire == null || amount <= 0)
            return;
        int before = _grimoire.EssenceCurrent;
        _grimoire.EssenceCurrent = Mathf.Min(_grimoire.EssenceCurrent + amount, _grimoire.EssenceMax);
        if (_grimoire.EssenceCurrent != before)
        {
            SaveManager.MarkDirty();
            GD.Print($"[Grimoire] +{_grimoire.EssenceCurrent - before} Essence ({source}) → " +
                     $"{_grimoire.EssenceCurrent}/{_grimoire.EssenceMax}.");
        }
    }

    /// <summary>Full restore (Outpost checkpoint).</summary>
    public void RestoreEssenceFull()
    {
        if (_grimoire == null)
            return;
        _grimoire.EssenceCurrent = _grimoire.EssenceMax;
        SaveManager.MarkDirty();
    }
}
