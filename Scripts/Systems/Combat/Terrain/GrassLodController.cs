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
// LOD curve (distances are camera -> closest point of the chunk AABB,
// divided by CameraController.LensDistanceScale so they track the shader
// fade under any lens; camera_lens_slice1):
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
        public int SolidApplied = -1;      // -1 = not yet decided, 0 = fading material, 1 = solid
    }

    // ── Solid pass ──────────────────────────────────────────────
    // The grass shader fades blades in and out by discarding pixels. A shader that
    // can discard is treated as alpha-tested, so the GPU cannot skip blades hidden
    // behind other blades. Chunks wholly inside the full-visibility range never
    // fade, so they are switched to a copy of the shader with the discard compiled
    // out. The two materials are kept identical otherwise: every parameter is
    // copied across each frame, so wind, tide, glyph and imbuement changes made on
    // the original reach the copy.

    private ShaderMaterial _fadingMaterial;
    private ShaderMaterial _solidMaterial;
    private readonly List<StringName> _uniformNames = new();
    private float _fadeStart = 18f;       // shader defaults, replaced by the material's values
    private float _nearFadeStart = 2.8f;
    private bool _distanceFade = true;
    private bool _nearFade = true;

    private static Shader _solidShaderSource;   // the shader the cached copy was built from
    private static Shader _solidShader;

    /// <summary>Builds the no-discard copy of the grass material. Call once, after the node
    /// is created and before chunks register. Does nothing if the shader has no source.</summary>
    public void ConfigureSolidPass(ShaderMaterial fading)
    {
        if (fading?.Shader == null)
            return;

        if (_solidShader == null || !ReferenceEquals(_solidShaderSource, fading.Shader))
        {
            const string header = "shader_type spatial;";
            string code = fading.Shader.Code;
            int at = string.IsNullOrEmpty(code) ? -1 : code.IndexOf(header, System.StringComparison.Ordinal);
            if (at < 0)
                return;

            at += header.Length;
            _solidShader = new Shader { Code = code.Substring(0, at) + "\n#define GRASS_SOLID\n" + code.Substring(at) };
            _solidShaderSource = fading.Shader;
        }

        _fadingMaterial = fading;
        _solidMaterial = (ShaderMaterial)fading.Duplicate();
        _solidMaterial.Shader = _solidShader;

        _uniformNames.Clear();
        foreach (Godot.Collections.Dictionary uniform in fading.Shader.GetShaderUniformList())
            _uniformNames.Add(uniform["name"].AsStringName());

        _fadeStart = ReadFloat(fading, "fade_start", _fadeStart);
        _nearFadeStart = ReadFloat(fading, "near_fade_start", _nearFadeStart);
        _distanceFade = ReadBool(fading, "use_distance_fade", true);
        _nearFade = ReadBool(fading, "use_near_fade", true);
    }

    private static float ReadFloat(ShaderMaterial material, string name, float fallback)
    {
        Variant v = material.GetShaderParameter(name);
        return v.VariantType == Variant.Type.Float || v.VariantType == Variant.Type.Int ? v.AsSingle() : fallback;
    }

    private static bool ReadBool(ShaderMaterial material, string name, bool fallback)
    {
        Variant v = material.GetShaderParameter(name);
        return v.VariantType == Variant.Type.Bool ? v.AsBool() : fallback;
    }

    /// <summary>Copies every shader parameter from the fading material to the solid one.</summary>
    private void SyncSolidMaterial()
    {
        if (_solidMaterial == null || _fadingMaterial == null)
            return;
        foreach (StringName name in _uniformNames)
            _solidMaterial.SetShaderParameter(name, _fadingMaterial.GetShaderParameter(name));
    }

    private readonly List<Entry> _entries = new();
    private int _lastSolidReported = -1;
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
        SyncSolidMaterial();   // every frame: tide and glyph changes animate

        _accum += delta;
        if (_accum < UpdateInterval)
            return;
        _accum = 0.0;

        var cam = GetViewport()?.GetCamera3D();
        if (cam == null)
            return;
        Vector3 camPos = cam.GlobalPosition;
        // Lens compensation: FullDistance/EndDistance are tuned for a 75 degree lens,
        // like the shader fade they must stay behind. A narrower lens sits the camera
        // farther back for the same framing, so measure in reference-lens terms.
        float lens = CameraController.LensDistanceScale(cam);

        float span = Mathf.Max(0.001f, EndDistance - FullDistance);
        int solidCount = 0;

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
            float d = camPos.DistanceTo(nearest) / lens;

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

            if (target != e.Applied)
            {
                e.Mmi.Multimesh.VisibleInstanceCount = target;
                e.Applied = target;
            }

            if (_solidMaterial == null)
                continue;

            // Solid only when the chunk's FARTHEST point is still short of the fade and
            // its nearest point is beyond the near fade, with a margin on both.
            Vector3 farthest = new Vector3(
                Mathf.Abs(camPos.X - e.Bounds.Position.X) > Mathf.Abs(camPos.X - e.Bounds.End.X) ? e.Bounds.Position.X : e.Bounds.End.X,
                Mathf.Abs(camPos.Y - e.Bounds.Position.Y) > Mathf.Abs(camPos.Y - e.Bounds.End.Y) ? e.Bounds.Position.Y : e.Bounds.End.Y,
                Mathf.Abs(camPos.Z - e.Bounds.Position.Z) > Mathf.Abs(camPos.Z - e.Bounds.End.Z) ? e.Bounds.Position.Z : e.Bounds.End.Z);
            bool insideFar = !_distanceFade || camPos.DistanceTo(farthest) / lens < _fadeStart * 0.9f;
            bool beyondNear = !_nearFade || camPos.DistanceTo(nearest) > _nearFadeStart * 1.25f;
            int solid = insideFar && beyondNear ? 1 : 0;
            solidCount += solid;
            if (solid != e.SolidApplied)
            {
                e.Mmi.MaterialOverride = solid == 1 ? _solidMaterial : _fadingMaterial;
                e.SolidApplied = solid;
            }
        }

        // Say how many chunks took the solid shader, when that number changes.
        if (_solidMaterial != null && solidCount != _lastSolidReported)
        {
            _lastSolidReported = solidCount;
            GD.Print($"[GrassLod] {solidCount} of {_entries.Count} grass chunk(s) on the solid shader (fade starts at {_fadeStart:0.#}).");
        }
    }
}
