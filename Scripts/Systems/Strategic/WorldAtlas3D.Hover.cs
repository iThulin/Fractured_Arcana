using Godot;

// ============================================================
// WorldAtlas3D.Hover.cs: the tile under the pointer (2026-11-03)
//
// Purpose:        Reports each change of the world tile under the
//                 pointer, so the strategic view can show what the
//                 pointer is over without a click: the province
//                 readout (docs/provinces_v1.md section 7c). Pointer
//                 motion only. A drag or an orbit owns the pointer and
//                 reports nothing, and city view reports no tile.
// Layer:          UI (strategic view)
// Collaborators:  WorldAtlas3D.cs (_UnhandledInput calls UpdateHover;
//                 TileOrigin, the layout spacing), StrategicView.
//                 ProvinceHover.cs (listens to TileHovered, calls
//                 ClearHover when the pointer leaves the map)
// See:            docs/provinces_v1.md section 7c
// ============================================================

public partial class WorldAtlas3D
{
    /// <summary>Fired when the world tile under the pointer changes, with its offset
    /// coordinates, or (-1, -1) when the pointer is over no tile: off the map, or in city
    /// view. A plain .NET event, like <see cref="TilePicked"/>.</summary>
    public event System.Action<int, int> TileHovered;

    private int _hoverCol = -1;
    private int _hoverRow = -1;

    /// <summary>From _UnhandledInput, on pointer motion that is neither a drag nor an orbit.
    /// Reports only a change of tile.</summary>
    private void UpdateHover(Vector2 screenPos)
    {
        int col = -1, row = -1;
        if (!_cityMode)
            TryTileAt(screenPos, out col, out row);
        if (col == _hoverCol && row == _hoverRow)
            return;
        _hoverCol = col;
        _hoverRow = row;
        TileHovered?.Invoke(col, row);
    }

    /// <summary>Forget the tile under the pointer and report (-1, -1), so the next motion
    /// reports its tile afresh. The host calls it when the pointer leaves the map, or when
    /// a drag starts.</summary>
    public void ClearHover()
    {
        if (_hoverCol < 0 && _hoverRow < 0)
            return;
        _hoverCol = -1;
        _hoverRow = -1;
        TileHovered?.Invoke(-1, -1);
    }

    /// <summary>The tile under a position in the atlas's viewport: the pointer's ray onto
    /// the y = 0 ground plane, then the nearest tile centre in the 3 x 3 offset
    /// neighbourhood, the same pick a click makes (PickTile). False when the ray misses the
    /// ground or no world is shown.</summary>
    public bool TryTileAt(Vector2 screenPos, out int col, out int row)
    {
        col = -1;
        row = -1;
        if (_world == null || _camera == null)
            return false;
        Vector3 origin = _camera.ProjectRayOrigin(screenPos);
        Vector3 dir = _camera.ProjectRayNormal(screenPos);
        if (Mathf.Abs(dir.Y) < 0.0001f)
            return false;
        float t = -origin.Y / dir.Y;
        if (t < 0)
            return false;
        Vector3 hit = origin + dir * t;

        int col0 = Mathf.RoundToInt(hit.X / ColSpacing);
        int row0 = Mathf.RoundToInt(hit.Z / RowSpacing);
        float bestD = float.MaxValue;
        for (int dc = -1; dc <= 1; dc++)
        {
            for (int dr = -1; dr <= 1; dr++)
            {
                int c = col0 + dc, r = row0 + dr;
                if (!_world.InBounds(c, r))
                    continue;
                Vector3 p = TileOrigin(c, r);
                float d = new Vector2(p.X - hit.X, p.Z - hit.Z).LengthSquared();
                if (d < bestD)
                {
                    bestD = d;
                    col = c;
                    row = r;
                }
            }
        }
        return col >= 0;
    }
}
