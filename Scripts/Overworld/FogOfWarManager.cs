using Godot;
using System.Collections.Generic;

/// <summary>
/// Manages fog state across the overworld hex grid.
/// Reveals hexes within vision radius of the party.
/// Phase 1: simple radius reveal. Phase 2+ adds intel, school abilities, persistence.
/// </summary>
public partial class FogOfWarManager : Node2D
{
    [Export] public int BaseVisionRadius = 1;

    private OverworldHexGrid _grid;

    public override void _Ready()
    {
        _grid = GetParent<OverworldHexGrid>();
        if (_grid == null)
            GD.PrintErr("FogOfWarManager: must be a child of OverworldHexGrid");
    }

    /// <summary>
    /// Call this whenever the party moves. Reveals hexes within vision radius
    /// and sets silhouettes on the fringe.
    /// </summary>
    public void UpdateVision(Vector2I partyCoord, int bonusRadius = 0)
    {
        int revealRange = BaseVisionRadius + bonusRadius;
        int silhouetteRange = revealRange + 1;

        foreach (var kvp in _grid.Hexes)
        {
            var coord = kvp.Key;
            var hex = kvp.Value;
            int dist = _grid.Distance(partyCoord, coord);

            if (dist <= revealRange)
            {
                // Full reveal — terrain, POIs, everything visible
                hex.Fog = OverworldHex.FogState.Revealed;
            }
            else if (dist <= silhouetteRange && hex.Fog == OverworldHex.FogState.Hidden)
            {
                // Silhouette — can see terrain shape but not POI content
                hex.Fog = OverworldHex.FogState.Silhouette;
            }
            // Note: already-revealed hexes stay revealed (no re-fogging)

            hex.RefreshVisuals();
        }
    }

    /// <summary>
    /// Make the objective landmark always visible through fog (as a silhouette).
    /// Per the design doc: "its general direction is always known."
    /// </summary>
    public void RevealLandmarks()
    {
        var objCoord = _grid.ObjectiveCoord;
        if (_grid.Hexes.TryGetValue(objCoord, out var objHex))
        {
            if (objHex.Fog == OverworldHex.FogState.Hidden)
                objHex.Fog = OverworldHex.FogState.Silhouette;
            objHex.RefreshVisuals();
        }
    }

    /// <summary>
    /// Reveal a specific hex fully (used by intel systems in Phase 2+).
    /// </summary>
    public void RevealHex(Vector2I coord)
    {
        if (_grid.Hexes.TryGetValue(coord, out var hex))
        {
            hex.Fog = OverworldHex.FogState.Revealed;
            hex.RefreshVisuals();
        }
    }
}