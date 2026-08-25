using Godot;
using System.Collections.Generic;

// ============================================================
// GrassLodController.cs
//
// Purpose:        Distance-density LOD for the chunked painterly grass.
//                 Each grass chunk MultiMesh registers itself with its
//                 bounds; on a slow tick this node measures the camera's
//                 distance to each chunk and thins far chunks by lowering
//                 MultiMesh.VisibleInstanceCount. Because the spawner
//                 SHUFFLES each chunk's instance buffer, any prefix of it
//                 is a spatially uniform subsample, so lowering the
//                 visible count thins the chunk evenly instead of erasing
//                 whole tiles off the buffer tail.
// Layer:          Combat / Terrain
// Collaborators:  HexGridManager.PainterlyGrass.cs (creates this node per
//                 spawn, registers every chunk, sets the distances from
//                 its exports; the node lives in the painterly_grass
//                 group, so ClearPainterlyGrass frees it with the field).
//
// LOD curve (distances are camera -> closest point of the chunk AABB):
//   d <= FullDistance                : 100% of blades (VisibleInstanceCount -1)
//   FullDistance < d < EndDistance   : lerp 100% -> MinFraction
//   d >= EndDistance                 : 0 blades. EndDistance must be >= the
//                                      grass material's fade_end. The dither
//                                      fade has fully dissolved blades by
//                                      then, so the zero cut is invisible.
//
// The tick is deliberately coarse (UpdateInterval). Setting
// VisibleInstanceCount is cheap, but there is no reason to do it per frame:
// the fractions only change meaningfully when the camera travels.
// ============================================================

/// <summary>Runtime density LOD for chunked grass MultiMeshes. Registered chunks
/// draw fewer instances as the camera gets farther, and none at all beyond
/// <see cref="EndDistance"/> (where the shader's dither fade has already fully
/// dissolved them). Created and configured by <c>SpawnPainterlyGrass</c>.</summary>
public partial class GrassLodController : Node
{
    /// <summary>Camera distance up to which a chunk draws all of its blades.</summary>
    public float FullDistance = 24f;

    /// <summary>Camera distance at which a chunk draws zero blades. Keep >= the grass material's fade_end.</summary>
    public float EndDistance = 55f;

    /// <summary>Blade fraction a chunk tapers to just before the zero cut.</summary>
    public float MinFraction = 0.35f;

    /// <summary>Seconds between LOD re-evaluations.</summary>
    public float UpdateInterval = 0.2f;

    private sealed class Entry
    {
        public MultiMeshInstance3D Mmi;
        public Aabb Bounds;
        public int Total;
        public int Applied = int.MinValue; // last VisibleInstanceCount written (-1 = all)
    }

    private readonly List<Entry> _entries = new();
    private double _accum;

    /// <summary>Registers one grass chunk. <paramref name="bounds"/> is the chunk's
    /// CustomAabb in world space (instance transforms are world-space and the
    /// MultiMeshInstance3D sits at the identity transform).</summary>
    public void Register(MultiMeshInstance3D mmi, Aabb bounds)
    {
        if (mmi?.Multimesh == null)
            return;
        _entries.Add(new Entry
        {
            Mmi = mmi,
            Bounds = bounds,
            Total = mmi.Multimesh.InstanceCount
        });
    }

    public override void _Process(double delta)
    {
        _accum += delta;
        if (_accum < UpdateInterval)
            return;
        _accum = 0.0;

        var cam = GetViewport()?.GetCamera3D();
        if (cam == null)
            return;
        Vector3 camPos = cam.GlobalPosition;

        float span = Mathf.Max(0.001f, EndDistance - FullDistance);

        foreach (var e in _entries)
        {
            if (!IsInstanceValid(e.Mmi))
                continue;

            // Distance to the chunk's closest point, not its centre. A chunk
            // whose near edge is under the camera must never get thinned as if
            // it were centre-distance away.
            Vector3 nearest = new Vector3(
                Mathf.Clamp(camPos.X, e.Bounds.Position.X, e.Bounds.End.X),
                Mathf.Clamp(camPos.Y, e.Bounds.Position.Y, e.Bounds.End.Y),
                Mathf.Clamp(camPos.Z, e.Bounds.Position.Z, e.Bounds.End.Z));
            float d = camPos.DistanceTo(nearest);

            int target;
            if (d >= EndDistance)
            {
                target = 0;                       // dither fade already at zero here
            }
            else if (d <= FullDistance)
            {
                target = -1;                      // -1 = draw all instances
            }
            else
            {
                float f = Mathf.Lerp(1f, MinFraction, (d - FullDistance) / span);
                target = Mathf.Max(1, Mathf.RoundToInt(e.Total * f));
            }

            if (target == e.Applied)
                continue;
            e.Mmi.Multimesh.VisibleInstanceCount = target;
            e.Applied = target;
        }
    }
}
