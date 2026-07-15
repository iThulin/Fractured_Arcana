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

    private enum CastState { Idle, Targeting }

    private ExpeditionManager _expedition;
    private OverworldHexGrid _grid;
    private GrimoireState _grimoire;
    private string _school = "";

    private CastState _state = CastState.Idle;
    private OverworldSpellDefinition _targetingSpell;
    private readonly List<Vector2I> _validTargets = new();
    private readonly List<OverworldHex> _highlighted = new();

    /// <summary>EffectKeys the S2 dispatcher implements. A definition outside
    /// this set is authored-but-not-yet-built: shown greyed out.</summary>
    private static readonly HashSet<string> ImplementedKeys = new()
    {
        "force_path", "tremorsense", "scrying_lens", "verdant_passage",
        "ember_ward", "ley_tap",
        "mending_cant", "purifying_rite", "wayfarers_beacon", "campward",
    };

    /// <summary>Attunement keys the S2 pass applies. Others no-op (logged once).</summary>
    private static readonly HashSet<string> ImplementedAttunements = new() { "elemental_sense" };
    private bool _warnedUnbuiltAttunement = false;

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

        if (PlayerSession.DebugMode)
            foreach (var d in OverworldSpellRegistry.All.Values)
                if (ImplementedKeys.Contains(d.EffectKey))
                    Add(d);

        return result;
    }

    /// <summary>Full Essence cost right now: base + corrupted-ground surcharge
    /// (casting FROM a tile of corruption tier T costs +T, §5).</summary>
    public int CastCostOf(OverworldSpellDefinition def)
        => def.EssenceCost + CorruptionSurcharge();

    public int CorruptionSurcharge() => _expedition?.SpellCorruptionTierAtParty() ?? 0;

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
        return null;
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

        if (def.TargetingType == "Tile")
            BeginTargeting(def);
        else
            ResolveCast(def, _expedition.PartyLocal);
    }

    private void BeginTargeting(OverworldSpellDefinition def)
    {
        _validTargets.Clear();
        CollectValidTargets(def, _validTargets);
        if (_validTargets.Count == 0)
        {
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
            }
        }
    }

    /// <summary>Grid click while targeting. Returns true when the click was
    /// consumed (valid or not) so ExpeditionManager doesn't route it to
    /// movement; false when Idle.</summary>
    public bool HandleHexClicked(Vector2I axial)
    {
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
        if (_state != CastState.Targeting)
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
            hex.GetNodeOrNull("SpellTargetHighlight")?.QueueFree();
        _highlighted.Clear();
        _validTargets.Clear();
        _targetingSpell = null;
        _state = CastState.Idle;
        if (message != null)
            _expedition.SpellInfo(message);
    }

    // ════════════════════════════════════════════════════════════════════
    // Resolution + dispatch
    // ════════════════════════════════════════════════════════════════════

    private void ResolveCast(OverworldSpellDefinition def, Vector2I target)
    {
        // Re-validate at resolve — the surcharge can differ from panel time.
        string block = CastBlockReason(def);
        if (block != null)
        {
            _expedition.SpellInfo($"{def.Name}: {block}.");
            return;
        }

        int cost = CastCostOf(def);
        string result = Dispatch(def, target);
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
        SaveManager.MarkDirty();

        int surcharge = cost - def.EssenceCost;
        _expedition.SpellInfo($"{def.Name}: {result} (−{cost} Essence" +
                              $"{(surcharge > 0 ? $", {surcharge} of it corruption" : "")}.)");

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
    private string Dispatch(OverworldSpellDefinition def, Vector2I target)
    {
        var world = _expedition.WorldRef;
        var window = _expedition.WindowRef;

        switch (def.EffectKey)
        {
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
        }
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
