using Godot;

// ============================================================
// HexGridManager.SnowCover.cs  (partial of HexGridManager)
//
// Purpose:        Sets how much snow has settled on the board. One number,
//                 the global shader uniform "snow_cover" (0 = none, 1 = heavy),
//                 read by every shader that includes snow_cover.gdshaderinc:
//                 rocks get snow caps, stone tops and cliff ledges whiten.
// Layer:          System (combat terrain, visuals only, no rules)
// Collaborators:  HexGridManager.cs (calls ApplySnowCover once per map, after
//                 the atmosphere), MapRecipe.cs (AtmosphereSpec.SnowCover, the
//                 recipe key "snow_cover"), Assets/Shaders/snow_cover.gdshaderinc
// See:            docs/snow_ice_ground_v1.md section 3.3
//
// The global is declared in project.godot under [shader_globals]. It must stay
// there: a shader that names an undeclared global does not compile. There is no
// runtime check, because the engine only lists shader globals inside the editor.
// ============================================================

public partial class HexGridManager : Node3D
{
    private const string SnowCoverGlobal = "snow_cover";

    /// <summary>Current settled-snow amount, 0..1. Read-only mirror of the shader global.</summary>
    public float SnowCover { get; private set; }

    /// <summary>
    /// Pushes the settled-snow amount to every snow-aware material. Call with 0 on
    /// maps without snow: the global outlives the fight, so a map that does not set
    /// it would inherit the last one.
    /// </summary>
    public void ApplySnowCover(float amount)
    {
        SnowCover = Mathf.Clamp(amount, 0f, 1f);
        RenderingServer.GlobalShaderParameterSet(SnowCoverGlobal, SnowCover);
    }
}
