using Godot;

// ============================================================
// PoiSignal.cs
//
// Purpose:        Curiosity-gap telegraph for revealed-but-
//                 unentered Narrative POIs (discovery_loop_spec
//                 Layer A — "Signal"). A narrative site should
//                 read as an *anomaly worth the steps*, not a
//                 labelled "Narrative" marker. Maps
//                 (terrain, coordinate) to a stable flavour hint.
// Layer:          System (static; stateless)
// Collaborators:  ExpeditionManager.cs (hover tooltip),
//                 OverworldHex.cs (POIType / TerrainType)
// See:            discovery_loop_spec_v1 §Layer A
//
// The signal is derived deterministically from the axial
// coordinate — no per-POI save state — so a site keeps ONE
// consistent signal across hovers, while neighbouring sites in
// the same terrain read differently. Only Narrative POIs are
// transformed; functional POIs (Combat/Rest/…) keep their kind
// name, because a fight should read as a fight.
// ============================================================

/// <summary>Terrain-flavoured curiosity signals for narrative anomalies.</summary>
public static class PoiSignal
{
    /// <summary>Tooltip label for a revealed POI: the evocative signal for a
    /// Narrative anomaly, the plain kind name for every functional POI.</summary>
    public static string Label(OverworldHex.POIType poi, OverworldHex.TerrainType terrain, Vector2I axial)
        => poi == OverworldHex.POIType.Narrative ? Signal(terrain, axial) : poi.ToString();

    /// <summary>A stable, terrain-flavoured signal string for a narrative site.</summary>
    public static string Signal(OverworldHex.TerrainType terrain, Vector2I axial)
    {
        var pool = PoolFor(terrain);
        uint h = (uint)((axial.X * 73856093) ^ (axial.Y * 19349663));
        return pool[(int)(h % (uint)pool.Length)];
    }

    private static string[] PoolFor(OverworldHex.TerrainType t) => t switch
    {
        OverworldHex.TerrainType.Snow or
        OverworldHex.TerrainType.Tundra          => _cold,
        OverworldHex.TerrainType.Swamp or
        OverworldHex.TerrainType.Marsh or
        OverworldHex.TerrainType.Lake            => _fen,
        OverworldHex.TerrainType.Ruins or
        OverworldHex.TerrainType.ArcaneGround    => _arcane,
        OverworldHex.TerrainType.Forest          => _wood,
        OverworldHex.TerrainType.Volcanic        => _ash,
        OverworldHex.TerrainType.Desert          => _waste,
        OverworldHex.TerrainType.Mountain or
        OverworldHex.TerrainType.Hills           => _highland,
        _                                        => _generic,
    };

    private static readonly string[] _cold = {
        "a cold that shouldn't be here",
        "breath fogging where nothing breathes",
        "ice that never took the thaw",
    };
    private static readonly string[] _fen = {
        "carrion birds circling",
        "a stillness the frogs won't break",
        "something sunk that won't stay sunk",
    };
    private static readonly string[] _arcane = {
        "a faint arcane hum",
        "sigils that seem to watch back",
        "a resonance that matches no known ley-line",
    };
    private static readonly string[] _wood = {
        "the treeline holding its breath",
        "footprints that leave no track",
        "a path the branches make for you",
    };
    private static readonly string[] _ash = {
        "ash falling from a clear sky",
        "warmth with no fire to answer it",
        "glass where the ground was struck",
    };
    private static readonly string[] _waste = {
        "a mirage that keeps its shape",
        "bones arranged too neatly",
        "a wind that carries a voice",
    };
    private static readonly string[] _highland = {
        "a cairn no road explains",
        "a signal-fire long cold",
        "something watching from the ridge",
    };
    private static readonly string[] _generic = {
        "smoke with no camp beneath it",
        "a marker leaning off the path",
        "a quiet that asks to be broken",
    };
}
