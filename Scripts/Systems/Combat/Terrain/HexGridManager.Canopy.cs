using Godot;
using System.Collections.Generic;

// ============================================================
// HexGridManager.Canopy.cs  (partial of HexGridManager)
//
// Tall Ghibli forest canopy over Forest tiles — a MultiMesh layer like the
// rocks/flowers, but:
//   - LOW count per tile (1-3 big masses, not dozens).
//   - Clumps TOWARD the dense grass-clump masses (like flowers) so canopy forms
//     copses with clearings, not an even ceiling.
//   - Casts shadows (the floor dappling is most of the forest read).
//   - Per-instance custom data = (tint.rgb, permanentFlag.a):
//       .rgb  near-white palette tint, MULTIPLIES the canopy gradient.
//       .a    1.0 = PERMANENT (never fades for the unit-occlusion cutout) on
//             board-edge / impassable forest; 0.0 = clears when a unit stands
//             under it. Read by painterly_canopy as INSTANCE_CUSTOM.a.
//     Canopy reads INSTANCE_CUSTOM (not COLOR), so UseCustomData is set WITHOUT
//     UseColors — same as flowers/rocks; the COLOR-black gotcha only hits shaders
//     that multiply COLOR (grass), which this one doesn't.
//
// OWNS THE MATERIAL: resolves + caches one canopy ShaderMaterial (auto-injecting
// wind_noise and model_height), then hands that exact instance to the
// CanopyOcclusion feeder so the per-frame occluder writes hit the material the
// MultiMeshes actually render with. Never assign the feeder's material by hand.
//
// Reuses helpers from the other scatter partials (same partial class):
//   InsideHex(...), SampleGrassSurfaceY(...), GrassClumpFrequency, MapSeed,
//   AxialToWorld, HexDirs, HexRadius, Tiles, PropParent, WindNoise,
//   DensityPreset / MapDensityPreset.
//
// INTEGRATION: one line at the tail of GenerateMap(), AFTER the other scatters:
//       SpawnRockProps();
//           SpawnCanopyProps();   // canopy last — it also wires the feeder
// ============================================================

public partial class HexGridManager : Node3D
{
    [ExportGroup("Canopy Props")]
    [Export] public bool EnableCanopyProps = true;

    /// <summary>Explicit canopy shader. If null, loaded from PainterlyCanopyShaderPath.</summary>
    [Export] public Shader PainterlyCanopyShader;

    /// <summary>Fallback load path for the canopy shader.</summary>
    [Export] public string PainterlyCanopyShaderPath = "res://Assets/Shaders/painterly_canopy.gdshader";

    /// <summary>Explicit canopy material. If set, it's used AS-IS — you must set wind_noise AND model_height yourself, and it must be a ShaderMaterial (the feeder needs SetShaderParameter). Leave null to let the builder create one, wire wind_noise, and set model_height from the tallest mesh.</summary>
    [Export] public Material PainterlyCanopyMaterial;

    /// <summary>Pool of canopy mesh variants (rounded blob clusters). Each scatter point picks one (optionally weighted). REQUIRED — canopy is skipped if empty. Author them at a CONSISTENT object-space height (see header).</summary>
    [Export] public Mesh[] CanopyMeshes;

    /// <summary>Optional relative spawn weights, parallel to CanopyMeshes. Leave empty for equal odds.</summary>
    [Export] public float[] CanopyMeshWeights;

    [Export(PropertyHint.Range, "0,6,1")] public int CanopyPerTile = 2;
    [Export(PropertyHint.Range, "0.1,6.0,0.05")] public float CanopyScale = 1.0f;
    [Export(PropertyHint.Range, "0,0.8,0.05")] public float CanopyScaleJitter = 0.30f;

    /// <summary>0 = canopy scatters evenly; 1 = canopy appears only where the grass clump field is DENSE (copses with clearings). Uses the same noise field as grass/flowers.</summary>
    [Export(PropertyHint.Range, "0,1,0.05")] public float CanopyClumpBias = 0.7f;

    /// <summary>Lift each canopy slightly along +Y (usually 0 — canopy sits on its trunk base).</summary>
    [Export(PropertyHint.Range, "0,0.5,0.01")] public float CanopySurfaceOffset = 0.0f;

    /// <summary>Random tilt (radians). Big masses barely lean — keep this small.</summary>
    [Export(PropertyHint.Range, "0,0.4,0.01")] public float CanopyTiltJitter = 0.06f;

    /// <summary>Also place canopy on Grass tiles (off = Forest only).</summary>
    [Export] public bool CanopyOnGrass = false;

    /// <summary>Mark canopy on board-EDGE forest tiles (any missing neighbour) as PERMANENT — never fades, framing the arena as a clearing in deep woods.</summary>
    [Export] public bool CanopyPermanentOnEdge = true;

    /// <summary>Per-instance palette tint via custom data (MULTIPLIES the gradient — keep entries near-white). Turn OFF for one uniform canopy tone; the permanent flag is still written either way.</summary>
    [Export] public bool UseCanopyColorVariation = true;

    /// <summary>Near-white tints sampled per canopy. They MULTIPLY the shader's gradient, so they nudge tone (warm/cool/light/dark), not recolour.</summary>
    [Export]
    public Color[] CanopyPalette =
    {
        new Color(1.00f, 1.00f, 1.00f), // neutral
        new Color(0.96f, 1.00f, 0.90f), // warm / light
        new Color(0.90f, 0.98f, 0.95f), // cool
        new Color(0.86f, 0.92f, 0.82f)  // darker
    };

    private const string CanopyPropGroup = "canopy_props";
    private Material _painterlyCanopyMaterialCache;
    private CanopyOcclusionManager _canopyOcclusion;

    public void SpawnCanopyProps()
    {
        ClearCanopyProps();

        if (!EnableCanopyProps)
            return;

        var mat = ResolvePainterlyCanopyMaterial();
        if (mat == null)
            return;

        // ── Variant pool (skip nulls, align weights) ──
        var variantMeshes = new List<Mesh>();
        var variantWeights = new List<float>();
        if (CanopyMeshes != null)
        {
            for (int i = 0; i < CanopyMeshes.Length; i++)
            {
                if (CanopyMeshes[i] == null)
                    continue;
                variantMeshes.Add(CanopyMeshes[i]);
                float wgt = (CanopyMeshWeights != null && i < CanopyMeshWeights.Length)
                    ? Mathf.Max(0f, CanopyMeshWeights[i])
                    : 1f;
                variantWeights.Add(wgt);
            }
        }
        if (variantMeshes.Count == 0)
        {
            GD.PushWarning("[HexGridManager] CanopyMeshes empty — canopy skipped. Assign at least one canopy blob mesh.");
            return;
        }

        int variantCount = variantMeshes.Count;
        float totalWeight = 0f;
        foreach (float wgt in variantWeights)
            totalWeight += wgt;

        // Auto-set model_height from the tallest variant so the shader's height
        // gradient / stiffness normalise correctly (one shared material -> one
        // model_height; author variants at a consistent height, see header).
        float maxMeshHeight = 0.0001f;
        foreach (var m in variantMeshes)
            maxMeshHeight = Mathf.Max(maxMeshHeight, m.GetAabb().Size.Y);
        if (mat is ShaderMaterial smHeight)
            smHeight.SetShaderParameter("model_height", maxMeshHeight);

        float densityScalar = DensityPreset switch
        {
            MapDensityPreset.Sparse => 0.5f,
            MapDensityPreset.Standard => 1.0f,
            MapDensityPreset.Dense => 1.4f,
            MapDensityPreset.Wild => 1.8f,
            _ => 1.0f
        };

        var rng = new RandomNumberGenerator { Seed = (ulong)(MapSeed ^ 0x43_41_4E_50) }; // "CANP"

        // Same clump field as grass/flowers, so canopy masses sit where grass is dense.
        var clumpNoise = new FastNoiseLite
        {
            Seed = unchecked(MapSeed ^ 0x13577531),
            Frequency = GrassClumpFrequency,
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth
        };

        var nbrDir = new Vector2[6];
        for (int k = 0; k < 6; k++)
        {
            Vector3 d = AxialToWorld(HexDirs[k]);
            nbrDir[k] = new Vector2(d.X, d.Z).Normalized();
        }
        Vector3 d0 = AxialToWorld(HexDirs[0]);
        float apothem = 0.5f * new Vector2(d0.X, d0.Z).Length();

        bool IsCanopySurface(TileData t) =>
            t.TerrainType == TileTerrainType.Forest ||
            (CanopyOnGrass && t.TerrainType == TileTerrainType.Grass);

        // Edge tile = at least one neighbour coord missing from the grid.
        bool IsBoundaryTile(TileData t)
        {
            for (int k = 0; k < 6; k++)
                if (!Tiles.ContainsKey(t.Axial + HexDirs[k]))
                    return true;
            return false;
        }

        bool paletteOk = UseCanopyColorVariation && CanopyPalette != null && CanopyPalette.Length > 0;

        // Custom data is ALWAYS written (it carries the permanent flag even when
        // colour variation is off), so every bucket collects a custom-data Color.
        var tfBuckets = new List<Transform3D>[variantCount];
        var customBuckets = new List<Color>[variantCount];
        for (int v = 0; v < variantCount; v++)
        {
            tfBuckets[v] = new List<Transform3D>();
            customBuckets[v] = new List<Color>();
        }

        int PickVariant()
        {
            if (variantCount == 1)
                return 0;
            if (totalWeight <= 0f)
                return rng.RandiRange(0, variantCount - 1);
            float r = rng.RandfRange(0f, totalWeight);
            float acc = 0f;
            for (int v = 0; v < variantCount; v++)
            {
                acc += variantWeights[v];
                if (r <= acc)
                    return v;
            }
            return variantCount - 1;
        }

        foreach (var tile in Tiles.Values)
        {
            if (tile.TileView == null || tile.IsBlocked || !IsCanopySurface(tile))
                continue;

            // Permanent where no unit ever reasonably stands: board edge or
            // height-/walk-blocked forest. Computed per TILE (all its canopy
            // shares the flag), so an edge copse never fades.
            bool permanent =
                (CanopyPermanentOnEdge && IsBoundaryTile(tile))
                || tile.BlocksMovementByHeight
                || !tile.IsWalkable;
            float permFlag = permanent ? 1f : 0f;

            int count = Mathf.Max(0, Mathf.RoundToInt(CanopyPerTile * densityScalar));
            Vector3 top = tile.TileView.GlobalPosition;

            for (int i = 0; i < count; i++)
            {
                Vector2 p;
                int guard = 0;
                do
                {
                    float ang = rng.RandfRange(0f, Mathf.Tau);
                    float radc = HexRadius * Mathf.Sqrt(rng.Randf());
                    p = new Vector2(Mathf.Cos(ang) * radc, Mathf.Sin(ang) * radc);
                    guard++;
                }
                while (!InsideHex(p, nbrDir, apothem) && guard < 8);
                if (!InsideHex(p, nbrDir, apothem))
                    continue;

                float wx = top.X + p.X;
                float wz = top.Z + p.Y;

                // Dense-mass bias: prefer HIGH clump noise (copses), like flowers.
                if (CanopyClumpBias > 0f)
                {
                    float cn = clumpNoise.GetNoise2D(wx, wz) * 0.5f + 0.5f; // 0..1
                    float accept = Mathf.Lerp(1f, cn, CanopyClumpBias);
                    if (rng.Randf() > accept)
                        continue;
                }

                float sy = SampleGrassSurfaceY(tile, wx, wz) + CanopySurfaceOffset;
                var pos = new Vector3(wx, sy, wz);

                float yaw = rng.RandfRange(0f, Mathf.Tau);
                float tiltA = rng.RandfRange(0f, CanopyTiltJitter);
                float tiltDir = rng.RandfRange(0f, Mathf.Tau);
                float s = Mathf.Max(0.05f, CanopyScale * (1f + rng.RandfRange(-CanopyScaleJitter, CanopyScaleJitter)));

                var basis = new Basis(Vector3.Up, yaw);
                var tiltAxis = new Vector3(Mathf.Cos(tiltDir), 0f, Mathf.Sin(tiltDir));
                basis = new Basis(tiltAxis, tiltA) * basis;
                basis = basis.Scaled(new Vector3(s, s, s));

                int variant = PickVariant();
                tfBuckets[variant].Add(new Transform3D(basis, pos));

                Color tint = paletteOk
                    ? CanopyPalette[rng.RandiRange(0, CanopyPalette.Length - 1)]
                    : Colors.White;
                // (tint.rgb, permanentFlag) -> INSTANCE_CUSTOM
                customBuckets[variant].Add(new Color(tint.R, tint.G, tint.B, permFlag));
            }
        }

        Node parent = PropParent ?? this;
        for (int v = 0; v < variantCount; v++)
        {
            var tf = tfBuckets[v];
            if (tf.Count == 0)
                continue;

            var cd = customBuckets[v];
            var mm = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                Mesh = variantMeshes[v],
                UseCustomData = true   // canopy reads INSTANCE_CUSTOM, not COLOR — no UseColors needed
            };
            mm.InstanceCount = tf.Count;

            for (int i = 0; i < tf.Count; i++)
            {
                mm.SetInstanceTransform(i, tf[i]);
                mm.SetInstanceCustomData(i, cd[i]);
            }

            // Explicit AABB — same reasoning as the grass: world-space scattered
            // instances frustum-cull as a unit and wind sway pushes geometry past
            // the auto-box. Canopy is TALL, so grow generously by mesh height.
            Vector3 mn = tf[0].Origin;
            Vector3 mx = mn;
            for (int i = 0; i < tf.Count; i++)
            {
                Vector3 o = tf[i].Origin;
                mn.X = Mathf.Min(mn.X, o.X);
                mn.Y = Mathf.Min(mn.Y, o.Y);
                mn.Z = Mathf.Min(mn.Z, o.Z);
                mx.X = Mathf.Max(mx.X, o.X);
                mx.Y = Mathf.Max(mx.Y, o.Y);
                mx.Z = Mathf.Max(mx.Z, o.Z);
            }
            float worldCanopyHeight = variantMeshes[v].GetAabb().Size.Y * CanopyScale * (1f + CanopyScaleJitter);
            float grow = Mathf.Max(4.0f, worldCanopyHeight * 1.5f + 2.0f);
            mm.CustomAabb = new Aabb(mn, mx - mn).Grow(grow);

            var mmi = new MultiMeshInstance3D
            {
                Name = $"CanopyPropField_{v}",
                Multimesh = mm,
                MaterialOverride = mat,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.On // canopy shadow = floor dappling
            };
            mmi.AddToGroup(CanopyPropGroup);
            parent.AddChild(mmi);
            mmi.GlobalTransform = Transform3D.Identity;
        }

        // Wire (or re-wire) the occlusion feeder to THIS material instance, so its
        // per-frame occluder writes land on the material the canopy renders with.
        if (mat is ShaderMaterial smFeed)
            EnsureCanopyOcclusion(smFeed);
    }

    private void EnsureCanopyOcclusion(ShaderMaterial mat)
    {
        Node parent = PropParent ?? this;

        if (_canopyOcclusion == null || !IsInstanceValid(_canopyOcclusion))
            _canopyOcclusion = parent.GetNodeOrNull<CanopyOcclusionManager>("CanopyOcclusionManager");

        if (_canopyOcclusion == null || !IsInstanceValid(_canopyOcclusion))
        {
            _canopyOcclusion = new CanopyOcclusionManager { Name = "CanopyOcclusionManager" };
            parent.AddChild(_canopyOcclusion);
        }

        _canopyOcclusion.SetCanopyMaterial(mat);
    }

    private void ClearCanopyProps()
    {
        Node parent = PropParent ?? this;
        foreach (Node child in parent.GetChildren())
        {
            if (child.IsInGroup(CanopyPropGroup))
                child.QueueFree();
        }
        // The CanopyOcclusion node is NOT in the group — it persists across regens
        // and is re-pointed at the fresh material by EnsureCanopyOcclusion.
    }

    private ShaderMaterial ResolvePainterlyCanopyMaterial()
    {
        if (PainterlyCanopyMaterial is ShaderMaterial explicitSm)
            return explicitSm; // used as-is: you must set wind_noise + model_height

        if (PainterlyCanopyMaterial != null)
        {
            GD.PushWarning("[HexGridManager] PainterlyCanopyMaterial is not a ShaderMaterial — the occlusion feeder needs one. Canopy skipped.");
            return null;
        }

        if (_painterlyCanopyMaterialCache is ShaderMaterial cached)
            return cached;

        Shader shader = PainterlyCanopyShader;
        if (shader == null && !string.IsNullOrEmpty(PainterlyCanopyShaderPath))
            shader = GD.Load<Shader>(PainterlyCanopyShaderPath);

        if (shader == null)
        {
            GD.PushWarning($"[HexGridManager] Painterly canopy shader not found " +
                $"(assign PainterlyCanopyShader, or place it at '{PainterlyCanopyShaderPath}'). Canopy skipped.");
            return null;
        }

        var sm = new ShaderMaterial { Shader = shader };
        sm.SetShaderParameter("wind_noise", WindNoise.CreateSeamless());
        _painterlyCanopyMaterialCache = sm;
        return sm;
    }
}
