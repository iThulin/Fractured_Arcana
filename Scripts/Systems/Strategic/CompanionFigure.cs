using Godot;

// ============================================================
// CompanionFigure.cs: one small figure per assigned person (2026-09-24)
//
// Purpose:        The visual for "who is assigned here". Every piece on the
//                 strategic map (the castle, a party, a detachment's banner)
//                 shows one figure per member standing with it, tinted by
//                 school. Built to take the shared animation rig the moment
//                 it lands: if res://Assets/Units/base_humanoid.glb exists it
//                 is instanced and tinted through the M_Tint material contract
//                 (unit_visual_pipeline_v1 section 3.2); until then a
//                 procedural stand-in of the same height stands in its place,
//                 so nothing in the callers changes when the art arrives.
// Layer:          Systems / Strategic (presentation only; reads nothing from
//                 the save, writes nothing)
// Collaborators:  WorldAtlas3D (placement), CompanionWhereabouts (tints)
// ============================================================

public static class CompanionFigure
{
    /// <summary>The shared rig, per unit_visual_pipeline_v1 section 11.1.</summary>
    public const string RigScenePath = "res://Assets/Units/base_humanoid.glb";

    /// <summary>The rig is authored at 1.75 m. On the strategic map a tile is
    /// about two units across, so a person stands at roughly a third of a
    /// tile: readable when zoomed, a fleck when not, which is right.</summary>
    public const float MapScale = 0.42f;

    /// <summary>Height of the procedural stand-in at MapScale 1. Matches the
    /// rig's 1.75 m so the swap does not change silhouettes.</summary>
    public const float StandInHeight = 1.75f;

    private static PackedScene _rig;
    private static bool _rigChecked;

    /// <summary>A figure to add under a marker root. Position it yourself.</summary>
    public static Node3D Build(Color tint, bool injured, float scale = MapScale)
    {
        var rig = LoadRig();
        Node3D figure = rig != null ? BuildRigged(rig, tint, injured) : BuildStandIn(tint, injured);
        figure.Scale = new Vector3(scale, scale, scale);
        return figure;
    }

    private static PackedScene LoadRig()
    {
        if (_rigChecked)
        {
            return _rig;
        }
        _rigChecked = true;
        if (ResourceLoader.Exists(RigScenePath))
        {
            _rig = ResourceLoader.Load<PackedScene>(RigScenePath);
            GD.Print(_rig != null
                ? "[CompanionFigure] Shared rig found; map figures use it."
                : "[CompanionFigure] Rig path exists but did not load as a PackedScene; using the stand-in.");
        }
        return _rig;
    }

    /// <summary>The rig, tinted through its named material. Only M_Tint is
    /// recoloured (the pipeline's contract); skin and accent are left alone.
    /// If the rig carries an AnimationPlayer with an idle clip it plays
    /// looped, so a garrison stands rather than freezes.</summary>
    private static Node3D BuildRigged(PackedScene rig, Color tint, bool injured)
    {
        var inst = rig.Instantiate<Node3D>();
        Color c = injured ? tint.Darkened(0.45f) : tint;
        TintNamedMaterial(inst, "M_Tint", c);
        var anim = FindAnimationPlayer(inst);
        if (anim != null && anim.HasAnimation("idle"))
        {
            anim.Play("idle");
        }
        return inst;
    }

    private static void TintNamedMaterial(Node node, string materialName, Color tint)
    {
        if (node is MeshInstance3D mi && mi.Mesh != null)
        {
            int surfaces = mi.Mesh.GetSurfaceCount();
            for (int s = 0; s < surfaces; s++)
            {
                var mat = mi.GetActiveMaterial(s);
                if (mat != null && mat.ResourceName == materialName)
                {
                    // Duplicate so one figure's tint never bleeds into another
                    // instance sharing the imported material.
                    var own = (Material)mat.Duplicate();
                    if (own is StandardMaterial3D sm)
                    {
                        sm.AlbedoColor = tint;
                    }
                    mi.SetSurfaceOverrideMaterial(s, own);
                }
            }
        }
        foreach (var child in node.GetChildren())
        {
            TintNamedMaterial(child, materialName, tint);
        }
    }

    private static AnimationPlayer FindAnimationPlayer(Node node)
    {
        if (node is AnimationPlayer ap)
        {
            return ap;
        }
        foreach (var child in node.GetChildren())
        {
            var found = FindAnimationPlayer(child);
            if (found != null)
            {
                return found;
            }
        }
        return null;
    }

    /// <summary>The stand-in: a tapered six-sided body, a head, and a short
    /// base so it reads as standing on the ground. Same vocabulary as the
    /// castle and party tokens (six-sided prisms rotated 30 degrees), so the
    /// map stays one game until the rig replaces all of it at once.</summary>
    private static Node3D BuildStandIn(Color tint, bool injured)
    {
        var root = new Node3D();
        Color body = injured ? tint.Darkened(0.45f) : tint;
        float h = StandInHeight;

        // Body: shoulders narrower than the hips of a robe, 0.30 wide at most.
        root.AddChild(new MeshInstance3D
        {
            Mesh = new CylinderMesh
            {
                TopRadius = 0.13f,
                BottomRadius = 0.19f,
                Height = h * 0.66f,
                RadialSegments = 6,
                Rings = 0,
            },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = body,
                EmissionEnabled = true,
                Emission = body,
                EmissionEnergyMultiplier = injured ? 0.15f : 0.45f,
            },
            Position = new Vector3(0f, h * 0.33f, 0f),
            RotationDegrees = new Vector3(0f, 30f, 0f),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
        // Head: skin-neutral, so the tint reads as clothing rather than a
        // coloured blob.
        root.AddChild(new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 0.12f, Height = 0.24f, RadialSegments = 8, Rings = 5 },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = injured ? new Color(0.55f, 0.48f, 0.42f) : new Color(0.86f, 0.74f, 0.62f),
                Roughness = 0.9f,
            },
            Position = new Vector3(0f, h * 0.66f + 0.12f, 0f),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
        return root;
    }

    /// <summary>Where the n-th of <paramref name="count"/> figures stands
    /// around a piece: a ring at <paramref name="radius"/>, first figure
    /// toward the camera's default south so a lone member is in front.</summary>
    public static Vector3 RingOffset(int n, int count, float radius)
    {
        if (count <= 1)
        {
            return new Vector3(0f, 0f, radius);
        }
        float a = Mathf.Pi * 0.5f + (Mathf.Tau * n / count);
        return new Vector3(Mathf.Cos(a) * radius, 0f, Mathf.Sin(a) * radius);
    }
}
