using Godot;

// ============================================================
// WorldAtlas3D.Keeper.cs  (partial of WorldAtlas3D)
//
// Purpose:        The Keeper's mark on the map (docs/provinces_v1_10_
//                 keeper.md, slice PV6, PR79): a violet orb over the
//                 seat of the province it stands in, labelled with its
//                 next card. Cycle state, handed in by the strategic
//                 view like the warfronts, and drawn with the markers.
// Layer:          UI (strategic map)
// Collaborators:  WorldAtlas3D.cs (the marker layer calls
//                 AddKeeperMarker), StrategicView.Keeper.cs (SetKeeper)
// See:            docs/provinces_v1_10_keeper.md section 5
// ============================================================

public partial class WorldAtlas3D
{
    private Vector2I _keeperTile = new(-1, -1);
    private string _keeperLabel = "";

    /// <summary>The Keeper's violet: apart from every realm's colour and the war's red.</summary>
    private static readonly Color KeeperColor = new(0.58f, 0.32f, 0.80f);

    /// <summary>Where the Keeper's mark stands and what it says; (-1, -1) for none. Stored
    /// only: the next marker rebuild (the warfronts' hand-over) draws it.</summary>
    public void SetKeeper(Vector2I tile, string label)
    {
        _keeperTile = tile;
        _keeperLabel = label ?? "";
    }

    /// <summary>Draw the Keeper's mark, when it stands on the map.</summary>
    private void AddKeeperMarker()
    {
        if (_world == null || _keeperTile.X < 0 || !_world.InBounds(_keeperTile.X, _keeperTile.Y))
            return;
        AddMarker(new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 0.8f, Height = 1.6f, RadialSegments = 12, Rings = 8 },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = KeeperColor,
                EmissionEnabled = true, Emission = KeeperColor, EmissionEnergyMultiplier = 1.4f,
            },
            Position = MarkerPos(_keeperTile.X, _keeperTile.Y, 3.4f),
        });
        AddMarker(MakeLabel(_keeperLabel.Length > 0 ? _keeperLabel : "The Keeper", KeeperColor,
            MarkerPos(_keeperTile.X, _keeperTile.Y, 4.9f)));
    }
}
