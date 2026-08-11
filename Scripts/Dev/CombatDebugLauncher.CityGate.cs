using Godot;
using System;

// ============================================================
// CombatDebugLauncher.CityGate.cs  (partial — dev tooling)
//
// Purpose:        The "campus_gate (compiled)" entry in the Force
//                 battlefield dropdown: compiles the HOME campus's
//                 WallSiege gate-assault window live from the current
//                 save (HomeCityCombatSource -> CityBattlemapCompiler),
//                 registers the emitted recipe, and forces it — the
//                 build-order step-3 smoke test ("walk a combat map
//                 that is recognizably your campus"). Dev-only.
// Layer:          UI (dev overlay)
// Collaborators:  CityBattlemapCompiler, HomeCityCombatSource,
//                 MapRecipeRegistry.Register, MapRecipe.FromDict
// ============================================================

public partial class CombatDebugLauncher : CanvasLayer
{
    private const string CompiledGateLabel = "campus_gate (compiled)";
    private const string CompiledGateDefenseLabel = "campus_gate DEFENSE (hold the gate)";
    private const string CompiledBreachLabel = "campus_breach (compiled)";

    /// <summary>Fixed debug seed → identical map every launch, so visual
    /// tuning between launches is comparing like with like.</summary>
    private const ulong CompiledGateSeed = 0xC1717E;

    /// <summary>Compile + register + force the home gate window. Returns false
    /// (with the reason in the status label) if the save can't produce one —
    /// launch should be aborted, not fall back silently to a wrong map.
    /// <paramref name="defending"/>: home-defense orientation (player holds
    /// the gate courtyard, enemies attack up the approach) + a hold_zone
    /// objective sited on the gate gap.</summary>
    private bool TryForceCompiledGate(EncounterDefinition def, bool defending = false,
        bool breach = false)
    {
        var ledger = SaveManager.ActiveSave?.Ledger;
        if (ledger == null)
        {
            _status.Text = "compiled gate: no active save/ledger.";
            return false;
        }

        var city = new HomeCityCombatSource(ledger);
        if (!breach && city.GateCell == null)
        {
            _status.Text = "compiled gate: no placed gatehouse_yard on this save.";
            return false;
        }

        CityWindowResult win;
        try
        {
            win = breach
                ? CityBattlemapCompiler.CompileWallBreach(
                    city, CompiledGateSeed, defending: defending)
                : CityBattlemapCompiler.CompileGateAssault(
                    city, CompiledGateSeed, defending: defending);
        }
        catch (Exception e)
        {
            _status.Text = $"compiled gate: compiler threw — {e.Message}";
            return false;
        }

        var parsed = Json.ParseString(win.RecipeJson);
        var dict = parsed.AsGodotDictionary();
        if (dict == null || dict.Count == 0)
        {
            _status.Text = "compiled gate: emitted JSON failed to parse (see log).";
            GD.PushWarning($"[CityCompiler] unparseable recipe JSON:\n{win.RecipeJson}");
            return false;
        }

        MapRecipeRegistry.Register(MapRecipe.FromDict(dict));
        def.MapRecipe = win.RecipeId;

        if (defending)
        {
            // The Campus Defense objective: hold the door. Rounds/BreachLimit
            // are debug starting values — the real encounter defs own tuning.
            def.Objective = new CombatObjectiveDef
            {
                Kind = CombatObjectiveDef.KindHoldZone,
                Rounds = 8,
                BreachLimit = 2,
                ZoneAnchor = "gate",
                ZoneRadius = 2,
                Description = "Hold the gate",
            };
        }

        GD.Print($"[CityCompiler] forced '{win.RecipeId}': walls={win.WallTiles.Count} " +
                 $"stampTiles={win.StampTiles.Count} gap={win.GateGap.Count} " +
                 $"playerAnchor=({win.PlayerAnchor.q},{win.PlayerAnchor.r}) " +
                 $"enemyAnchor=({win.EnemyAnchor.q},{win.EnemyAnchor.r}) " +
                 $"defending={defending}");
        return true;
    }
}
