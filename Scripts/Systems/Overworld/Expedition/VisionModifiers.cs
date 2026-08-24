// ============================================================
// VisionModifiers.cs
//
// Purpose:        The single, shared scry/vision-radius modifier the
//                 expedition's two reveal paths both read, so a bonus or
//                 penalty is applied once and consistently:
//                   - FogOfWarManager.UpdateVision (the 2D fog path)
//                   - ExpeditionWindow3D.UpdateVision (the 3D scrying rig)
//
//                 Static, like the other overworld ambients
//                 (OverworldSpellEffects, WeatherSystem), so it survives the
//                 combat scene swap and both reveal paths see the same value
//                 without threading a parameter through three classes.
//
//                 W2 uses it for the weather scry penalty (fronts shrink the
//                 lens). It is deliberately an AGGREGATE bucket: the Arcanist
//                 castle's scry +1 (F3), the Lens Room crew station (F4), and
//                 the Farseeing Array module (F5) all add here too. The
//                 expedition sets ScryBonus each move; reveal ranges floor at
//                 0 (you always see your own tile).
// Layer:          System (static ambient; no nodes)
// Collaborators:  ExpeditionManager (writer), FogOfWarManager +
//                 ExpeditionWindow3D (readers).
// ============================================================

/// <summary>Shared additive scry/vision-radius modifier. Negative shrinks the
/// reveal radius (weather); positive widens it (Arcanist / Lens Room / Farseeing
/// Array). Both reveal paths add this to their base radius, floored at 0.</summary>
public static class VisionModifiers
{
    /// <summary>Added to every reveal radius this stride. Set by the expedition
    /// each move from the sum of active scry effects; 0 when nothing applies.</summary>
    public static int ScryBonus = 0;

    /// <summary>Clear the modifier (fresh deploy / run end), so a stale sortie's
    /// weather penalty never leaks into the next run's vision.</summary>
    public static void Reset() => ScryBonus = 0;
}
