using System.Collections.Generic;

// ============================================================
// StartScenario.cs
//
// Purpose:        Schema for a founding "starting scenario" — a
//                 curated, predetermined seeded option the player
//                 picks when founding the guild. Each scenario
//                 fixes a world seed and a bundle of difficulty
//                 levers; the choice is guild-level (survives cycle
//                 resets) and is re-applied to every cycle's world
//                 generation. Loaded once from
//                 Data/World/start_scenarios.json.
// Layer:          Data
// Collaborators:  StartScenarioLoader.cs (JSON parser),
//                 WorldGenerator.Params (levers feed in here),
//                 NewGameScreen.cs (founding picker — later phase),
//                 EternalLedger.cs (stores the chosen profile — later phase)
// See:            docs/world_locales_and_founding_spec_v1.md §3,
//                 docs/start_scenarios_curation_v1.md
// ============================================================

/// <summary>Fractional world-position hint (0..1 of Width/Height) for where the
/// guild's start capital should be placed. The generator snaps to the nearest
/// valid land tile near this point; null = the legacy interior-third random.</summary>
public class StartHint
{
    public float XFrac = 0.5f;
    public float YFrac = 0.5f;
}

/// <summary>One curated founding option. Difficulty is carried by explicit
/// levers, not by geometry alone — the generator re-normalises the difficulty
/// ramp around wherever the start lands, so a spawn coordinate on its own does
/// not vary difficulty (see spec §2). All numeric defaults reproduce the
/// current shipping behaviour, so an unspecified field is a no-op.</summary>
public class StartScenario
{
    // ── Identity / presentation ──────────────────────────────────────────
    public string Id = "";
    public string DisplayName = "";
    public string Blurb = "";

    /// <summary>Display band: "Gentle" | "Standard" | "Harsh" | "Brutal".</summary>
    public string DifficultyTag = "Standard";

    /// <summary>0..3 — sort order and star display.</summary>
    public int DifficultyRank = 1;

    // ── World identity ───────────────────────────────────────────────────
    /// <summary>Base seed. Deterministic: same seed reproduces the same map.
    /// Per-cycle worlds derive from this so later timelines differ (wiring phase).</summary>
    public int Seed = 0;

    /// <summary>"Pangaea" | "Continents" | "Archipelago", or null/empty to roll
    /// the style from the seed. Parsed to ContinentStyle by the caller.</summary>
    public string ContinentStyle = null;

    /// <summary>Where to place the start capital. Null = legacy random spawn.</summary>
    public StartHint StartHint = null;

    // ── Difficulty levers ────────────────────────────────────────────────
    /// <summary>One knob, two coupled effects (spec §3.2): scales where the
    /// Convergence lands (as a fraction of the max capital distance) AND the
    /// tier-ramp steepness. 1.0 = today; &lt;1 nearer + steeper; &gt;1 farther + gentler.</summary>
    public float ConvergenceDistanceBias = 1.0f;

    /// <summary>Runtime encounter scaling. Stamped onto the cycle for the run
    /// layer to read (wiring phase).</summary>
    public float EnemyDifficultyMult = 1.0f;

    /// <summary>Corruption tile-spread rate multiplier (wiring phase).</summary>
    public float CorruptionSpreadMult = 1.0f;

    /// <summary>Bootstrap staging outposts seeded near home (1..3). 2 = today.</summary>
    public int StartingOutposts = 2;

    /// <summary>POIs pre-discovered near the start. 3 = today.</summary>
    public int PreDiscoveredPois = 3;

    /// <summary>Founding stipend delta (added to the base starting gold).</summary>
    public int StartingGold = 0;

    /// <summary>Seeded PlayerInfluence at the home kingdom. 25 = today.</summary>
    public int StartInfluence = 25;

    // ── Authoring note (ignored by gameplay) ─────────────────────────────
    public string FlavorIntent = "";

    /// <summary>Map this scenario's generation levers onto a
    /// <see cref="WorldGenerator.Params"/>. The single source of the
    /// scenario→generation mapping — WorldDebug's validator and the cycle-world
    /// wiring both call this. Presentation fields and the RUNTIME mults
    /// (EnemyDifficultyMult / CorruptionSpreadMult, which are stamped onto
    /// CycleState and consumed by the combat + corruption layers) are not part of
    /// world generation and are intentionally excluded here.</summary>
    public WorldGenerator.Params ToWorldParams()
    {
        var p = new WorldGenerator.Params
        {
            PreDiscoveredPois = PreDiscoveredPois,
            ConvergenceDistanceBias = ConvergenceDistanceBias,
            StartingOutposts = StartingOutposts,
            StartInfluence = StartInfluence,
        };
        if (StartHint != null)
        {
            p.StartHintX = StartHint.XFrac;
            p.StartHintY = StartHint.YFrac;
        }
        if (!string.IsNullOrEmpty(ContinentStyle) &&
            System.Enum.TryParse<ContinentStyle>(ContinentStyle, true, out var cs))
            p.ContinentStyleOverride = cs;
        return p;
    }
}

/// <summary>Root object of Data/World/start_scenarios.json.</summary>
public class StartScenarioFile
{
    public int Version = 1;
    public List<StartScenario> Scenarios = new();
}
