using Godot;

// ============================================================
// SurroundTile.cs
//
// Purpose:        A bare stand-in for a HexTile on out-of-bounds ground in the
//                 wide surround (HexGridManager.Surround.cs). It is one empty
//                 node at the tile's position and height. It has no mesh, no
//                 collision, no label and no overlay: the surround's ground is
//                 drawn as one merged mesh, and nothing out there can be
//                 clicked, highlighted or stood on.
// Layer:          System (combat terrain)
// Collaborators:  HexGridManager.Vista.cs (creates these in place of the tile
//                 scene), and every system that reads a tile's position
//                 through TileData.TileView (grass, rocks, flowers, canopy,
//                 water, the far field).
// See:            docs/wide_surround_v1.md
//
// Why a subclass: those systems expect TileData.TileView to be a HexTile and
// read only its GlobalPosition. A HexTile built from the tile scene costs
// about six nodes, a material copy and a collision body. Fourteen rings of
// them would be over a thousand of each.
//
// Rule for callers: read the position only. The highlight, height and overlay
// methods of HexTile expect the scene's child nodes, which are not here. Set
// the height by setting Position.
// ============================================================

public partial class SurroundTile : HexTile
{
    public override void _Ready()
    {
        // Deliberately not calling base._Ready(): it fetches the tile scene's child
        // nodes, and this node has none.
        SetProcess(false);
    }
}
