using Godot;
using System.Collections.Generic;

// ============================================================
// ImbuementOverlay.cs
//
// Purpose:        Visual child of HexTile that renders the
//                 elemental imbuement effect: a coloured aura
//                 column rising from the tile plus a bobbing
//                 glyph hovering above it. Driven by element
//                 enum, tinted via shader parameters.
// Layer:          Tiles
// Collaborators:  HexTile.cs (parent; lazy-instantiates this),
//                 UITheme.cs (per-element tint colors),
//                 TileData.cs (TileElementType enum)
// See:            README §6, Elemental Attunement (for the
//                 element-to-gameplay mapping this visualizes)
// ============================================================

/// <summary>
/// Visual overlay rendered as a child of <see cref="HexTile"/>. Combines a coloured aura
/// column with a bobbing glyph mesh to indicate the tile's elemental imbuement. The two
/// meshes share a per-instance ShaderMaterial duplicate so each tile can carry its own
/// tint and element ID; <see cref="AutoFitToTile"/> measures the parent tile's mesh AABB
/// to anchor heights correctly regardless of tile scale or terrain height changes.
/// </summary>
public partial class ImbuementOverlay : Node3D
{
    /// <summary>Aura column mesh. Resolved from the "Aura" child node when not set in the inspector.</summary>
    [Export] public MeshInstance3D AuraMesh;

    /// <summary>Bobbing glyph mesh. Resolved from the "Glyph" child node when not set in the inspector.</summary>
    [Export] public MeshInstance3D GlyphMesh;

    /// <summary>
    /// Vertical multiplier on the elemental form. 1.0 = the heights authored in
    /// ImbuementForms' table (Fire 0.92 down to Water 0.20). This USED to be an absolute
    /// column height in world units; it is a multiplier now because the form's height is
    /// part of its identity. A fire and a puddle should not be the same size.
    /// </summary>
    [Export] public float AuraHeight = 1.0f;

    /// <summary>Uniform scale on the elemental form. Raise if the tile radius is not ~1.0.</summary>
    [Export] public float FormScale = 1.0f;

    /// <summary>
    /// Yaw applied to the form, degrees. Edge- and corner-placed shards (Frost, Earth) are
    /// laid out on a hex assumed to be aligned at 0°; if they land on the wrong bearing for
    /// this project's HexMesh, rotate here rather than editing the table.
    /// </summary>
    [Export] public float FormYawDegrees = 0f;

    /// <summary>Gap between the top of a prop form's tallest stone and the rune floating over it.</summary>
    [Export] public float GlyphRockClearance = 0.22f;

    /// <summary>Seconds for one stone to break the surface. The whole tile takes this plus the stagger.</summary>
    [Export] public float RockRiseSeconds = 1.15f;

    /// <summary>Fraction of a stone's window spent straining underground before it breaks through. The anticipation is what makes the launch read as force rather than as a lift.</summary>
    [Export] public float RockRumbleFraction = 0.38f;

    /// <summary>Lateral shudder while straining, world units. Small, because this is a heavy object failing to move, not a vibrating one.</summary>
    [Export] public float RockRumbleAmount = 0.035f;

    /// <summary>How far past its resting height a stone throws itself, as a fraction of its burial depth. It falls back and beds down after.</summary>
    [Export] public float RockOvershoot = 0.20f;

    /// <summary>
    /// Shudder frequency, radians per unit of rise time. Divide by RockRiseSeconds for Hz:
    /// 52 over 1.15 s is about 7 Hz, which is a heavy thing grinding. The first pass ran
    /// at 95 (~13 Hz) and read as a buzz. Mass is carried by LOW frequency, and a rock
    /// that vibrates is a small rock.
    /// </summary>
    [Export] public float RockRumbleFreq = 52f;

    /// <summary>Height above the tile top where the glyph's neutral position sits.</summary>
    [Export] public float GlyphBaseHeight = 0.55f;

    /// <summary>Vertical bob amplitude in world units.</summary>
    [Export] public float GlyphBobAmount = 0.08f;

    /// <summary>Bob frequency in radians per second.</summary>
    [Export] public float GlyphBobSpeed = 1.4f;

    /// <summary>When true (default), measure the parent HexTile's actual mesh AABB at startup and reposition the aura/glyph to match. Set false to use the scene's hardcoded transforms verbatim.</summary>
    [Export] public bool AutoFitToTile = true;

    /// <summary>Edge length in pixels of the baked element rune. Eight textures total, cached forever and shared by every tile, so this is cheap to raise.</summary>
    [Export] public int RunePixels = 256;

    /// <summary>Draw the runes with the hand-inked cipher renderer instead of the shader's analytic symbols. Turn off to A/B the two.</summary>
    [Export] public bool UseInkedRunes = true;

    // Cached after auto-fit: the local-space Y of the parent tile's top surface.
    private float _tileTopY = 0.0f;

    /// <summary>
    /// Boulder scatter for prop-based imbuements (Earth). One node per distinct mesh
    /// (a MultiMesh holds exactly one), because seven copies of a single rock, however
    /// rotated, reads as a stamp rather than as rubble.
    /// </summary>
    private readonly List<MultiMeshInstance3D> _rockNodes = new();

    /// <summary>Dirt clods thrown up around the stones. Same meshes, crushed flat and tinted.</summary>
    private readonly List<MultiMeshInstance3D> _debrisNodes = new();

    /// <summary>The scatter currently on this tile, including everything needed to replay the rise.</summary>
    private RockScatter _scatter;

    /// <summary>Rise clock in normalised units, or -1 when idle. Runs to 1 + ImbuementRocks.RiseSpread so the last-delayed stone still finishes.</summary>
    private float _riseT = -1f;

    /// <summary>Extra height the element rune is pushed up by, so it clears whatever the form put on the tile. 0 for the shard forms.</summary>
    private float _glyphLift;

    /// <summary>
    /// The grid that owns this tile, cached on first use. Everything about the rock
    /// look (pool, material, scale, sink, tilt) is configured on it, so the imbued
    /// tile's boulders match the map's own by construction instead of by matching
    /// numbers in two places.
    /// </summary>
    private HexGridManager _grid;
    private HexGridManager Grid => _grid ??= ImbuementRocks.FindGrid(this);

    private ShaderMaterial _auraMaterial;
    private ShaderMaterial _glyphMaterial;
    private TileElementType _current = TileElementType.None;
    private float _timeOffset;

    // Element → tint color (used by both shaders).
    private static readonly Dictionary<TileElementType, Color> ElementTints = new()
    {
        { TileElementType.Fire,      UITheme.ElementTintFire      },
        { TileElementType.Frost,     UITheme.ElementTintFrost     },
        { TileElementType.Lightning, UITheme.ElementTintLightning },
        { TileElementType.Earth,     UITheme.ElementTintEarth     },
        { TileElementType.Water,     UITheme.ElementTintWater     },
        { TileElementType.Air,       UITheme.ElementTintAir       },
        { TileElementType.Arcane,    UITheme.ElementTintArcane    },
        { TileElementType.Shadow,    UITheme.ElementTintShadow    },
    };

    // Element → integer ID for the shader switch.
    private static readonly Dictionary<TileElementType, int> ElementIds = new()
    {
        { TileElementType.Fire,      0 },
        { TileElementType.Frost,     1 },
        { TileElementType.Lightning, 2 },
        { TileElementType.Earth,     3 },
        { TileElementType.Water,     4 },
        { TileElementType.Air,       5 },
        { TileElementType.Arcane,    6 },
        { TileElementType.Shadow,    7 },
    };

    public override void _Ready()
    {
        if (AuraMesh == null) AuraMesh = GetNodeOrNull<MeshInstance3D>("Aura");
        if (GlyphMesh == null) GlyphMesh = GetNodeOrNull<MeshInstance3D>("Glyph");

        // Duplicate shader materials so each tile can have its own tint/id.
        if (AuraMesh != null)
        {
            var src = AuraMesh.GetActiveMaterial(0) as ShaderMaterial;
            if (src != null)
            {
                _auraMaterial = (ShaderMaterial)src.Duplicate();
                AuraMesh.SetSurfaceOverrideMaterial(0, _auraMaterial);
            }
        }

        if (GlyphMesh != null)
        {
            var src = GlyphMesh.GetActiveMaterial(0) as ShaderMaterial;
            if (src != null)
            {
                _glyphMaterial = (ShaderMaterial)src.Duplicate();
                GlyphMesh.SetSurfaceOverrideMaterial(0, _glyphMaterial);
            }
        }

        // Slight per-tile time offset so adjacent tiles don't bob in lockstep.
        _timeOffset = (float)GD.RandRange(0.0, Mathf.Tau);

        if (AutoFitToTile)
            FitToParentTile();

        Visible = false;
    }

    /// <summary>
    /// Measures the parent HexTile's mesh AABB to find the tile's actual top
    /// surface, then positions the aura column to sit on it and the glyph
    /// to hover at its base height above it. This makes the overlay robust
    /// to changes in tile geometry (Hex_mesh.tres scale, blocker variants,
    /// height adjustments via SetHeight, etc).
    /// </summary>
    private void FitToParentTile()
    {
        var parent = GetParent();
        if (parent == null) return;

        var hexMesh = parent.GetNodeOrNull<MeshInstance3D>("HexMesh");
        if (hexMesh == null || hexMesh.Mesh == null) return;

        // AABB is in the mesh's local space; we need it in the HexTile's
        // local space (which is the same coordinate space as our own
        // Position, since we're a sibling of HexMesh under HexTile).
        var aabb = hexMesh.Mesh.GetAabb();

        // Apply the HexMesh node's transform to the AABB to get it in
        // HexTile-local space. We only care about the top Y.
        var t = hexMesh.Transform;
        // Top of AABB in mesh-local: aabb.Position.Y + aabb.Size.Y
        // Transformed by HexMesh's local transform: scale and offset Y.
        float meshTopLocal = aabb.Position.Y + aabb.Size.Y;
        // HexMesh's transform basis Y scale + origin Y:
        float scaleY = t.Basis.Y.Length();
        float offsetY = t.Origin.Y;
        _tileTopY = meshTopLocal * scaleY + offsetY;

        // Position the aura: cylinder origin = tile top + half its height,
        // so its bottom is flush with the tile top.
        if (AuraMesh != null)
        {
            // ImbuementForms builds every shard with its base at y = 0, so the form
            // sits directly ON the measured tile top. The half-height offset and the
            // rescale-against-authored-height that used to live here existed only to
            // place a CylinderMesh centred on its own origin, and both were silent
            // sources of error the moment the mesh stopped being that cylinder.
            var pos = AuraMesh.Position;
            pos.Y = _tileTopY;
            AuraMesh.Position = pos;

            AuraMesh.Scale = new Vector3(FormScale, FormScale * AuraHeight, FormScale);
            AuraMesh.Rotation = new Vector3(0f, Mathf.DegToRad(FormYawDegrees), 0f);
        }

        foreach (var n in _rockNodes)
        {
            // Rocks take FormScale on all three axes, NOT AuraHeight. That export
            // stretches the shard forms, and a stretched boulder stops looking like
            // a rock the instant it is not uniform.
            var rp = n.Position;
            rp.Y = _tileTopY;
            n.Position = rp;
            n.Scale = new Vector3(FormScale, FormScale, FormScale);
            n.Rotation = new Vector3(0f, Mathf.DegToRad(FormYawDegrees), 0f);
        }

        // Glyph base position (script also drives the bob in _Process).
        if (GlyphMesh != null)
        {
            var pos = GlyphMesh.Position;
            pos.Y = _tileTopY + GlyphBaseHeight + _glyphLift;
            GlyphMesh.Position = pos;
        }
    }

    public override void _Process(double delta)
    {
        if (!Visible) return;

        if (_riseT >= 0f)
        {
            _riseT += (float)delta / Mathf.Max(0.01f, RockRiseSeconds);
            ApplyRise(_riseT);
            // Run past 1 by the stagger so the last-delayed stone still lands.
            if (_riseT >= 1f + ImbuementRocks.RiseSpread) _riseT = -1f;
        }

        if (GlyphMesh == null) return;

        // Bob the glyph up and down for the floating-magic feel.
        float t = (float)Time.GetTicksMsec() / 1000f + _timeOffset;
        float baseY = AutoFitToTile ? _tileTopY + GlyphBaseHeight : GlyphMesh.Position.Y;
        // (When AutoFitToTile=false, we just use the scene's authored Y
        // and bob around it; for that case we don't have _tileTopY cached,
        // so we keep the current Y as the bob anchor.)

        var pos = GlyphMesh.Position;
        if (AutoFitToTile)
            pos.Y = _tileTopY + GlyphBaseHeight + _glyphLift + Mathf.Sin(t * GlyphBobSpeed) * GlyphBobAmount;
        else
            pos.Y = baseY + Mathf.Sin(t * GlyphBobSpeed) * GlyphBobAmount;
        GlyphMesh.Position = pos;
    }

    /// <summary>Sets the displayed elemental imbuement. Pass <see cref="TileElementType.None"/> to hide the overlay. Updates both shader tint and element_id parameters so the shader can pick the right glyph/visual.</summary>
    public void SetElement(TileElementType element)
    {
        _current = element;

        if (element == TileElementType.None)
        {
            Visible = false;
            return;
        }

        Color tint = ElementTints.TryGetValue(element, out var c)
            ? c
            : new Color(1, 1, 1, 1);
        int id = ElementIds.TryGetValue(element, out var i) ? i : 0;

        _auraMaterial?.SetShaderParameter("tint_color", tint);
        _auraMaterial?.SetShaderParameter("element_id", id);

        _glyphMaterial?.SetShaderParameter("tint_color", tint);
        _glyphMaterial?.SetShaderParameter("element_id", id);

        ApplyElementalForm(element);
        RequestInkedRune(element);

        Visible = true;
    }

    /// <summary>
    /// Swaps in the element's silhouette (flames, crystals, plates, ribbons), built
    /// procedurally by <see cref="ImbuementForms"/>.
    ///
    /// A null form means "keep the mesh already there", never "show nothing": which
    /// element a tile carries is targetable, consumable gameplay state, and a tile with no
    /// marker at all is a gameplay bug rather than a cosmetic one. The surface override
    /// material set in _Ready survives the mesh swap. It is held by the MeshInstance3D,
    /// not by the Mesh resource, so the per-tile tint and element id are not disturbed.
    /// </summary>
    private void ApplyElementalForm(TileElementType element)
    {
        if (AuraMesh == null) return;

        // Prop elements (Earth) are PLACED, not drawn: real boulders from the
        // painterly rock pool, lit and shadowed by the same passes as every other
        // rock on the board. The shard form and the rock form are mutually
        // exclusive, and BOTH are hidden/shown every time. Leaving the loser
        // visible is how a tile ends up wearing two elements at once.
        bool useRocks = ImbuementRocks.HasRockForm(element);
        var scatter = useRocks ? ImbuementRocks.Build(element, GetInstanceId(), Grid) : null;
        var rockMat = useRocks ? ImbuementRocks.MaterialFor(Grid) : null;

        // Fall through to the shard form if the rock resources are missing, rather
        // than showing nothing: which element a tile carries is gameplay state.
        useRocks = useRocks && scatter != null && rockMat != null;

        if (useRocks)
        {
            _scatter = scatter;
            EnsureNodes(_rockNodes, "RockForm", scatter.Stones.Length);
            EnsureNodes(_debrisNodes, "RockDirt", scatter.Debris.Length);

            float ext = ImbuementRocks.ExtentOf(element, Grid) * Mathf.Max(FormScale, 0.0001f);
            // Extend the box DOWNWARD far enough to hold a fully buried stone, or the
            // scatter culls itself out of existence at the start of its own animation.
            var aabb = new Aabb(new Vector3(-ext, -2.0f, -ext), new Vector3(ext * 2f, 4.0f, ext * 2f));

            Bind(_rockNodes, scatter.Stones, rockMat, aabb);
            Bind(_debrisNodes, scatter.Debris, rockMat, aabb);

            // Push the rune clear of the stones. TopY is MEASURED from the meshes' own
            // AABBs, not estimated. The first pass left the rune at its usual height
            // and it ended up buried in the rubble, which loses the one thing keeping
            // Earth in the same family as the other seven elements.
            _glyphLift = Mathf.Max(0f, scatter.TopY * FormScale + GlyphRockClearance - GlyphBaseHeight);

            AuraMesh.Visible = false;
            RefreshGlyphHeight();

            // Play the uprooting. Restarted on every (re)imbue, which is correct: the
            // stones are being pushed up again.
            _riseT = 0f;
            ApplyRise(0f);
            return;
        }

        _scatter = null;
        _riseT = -1f;
        foreach (var n in _rockNodes) n.Visible = false;
        foreach (var n in _debrisNodes) n.Visible = false;
        _glyphLift = 0f;
        RefreshGlyphHeight();
        AuraMesh.Visible = true;

        var mesh = ImbuementForms.MeshFor(element);
        if (mesh == null) return;

        AuraMesh.Mesh = mesh;

        // The shader anchors the wind lean at the base by normalising VERTEX.y against
        // this. A stale value tilts the whole form rigidly instead of bending it, which
        // looks like a bug in the mesh rather than in a uniform.
        _auraMaterial?.SetShaderParameter("column_height", ImbuementForms.HeightOf(element));
    }

    /// <summary>
    /// Grows the pool of scatter nodes to <paramref name="want"/>. Nodes are never freed:
    /// a tile that has been Earth once will very likely be Earth again, and three spare
    /// MultiMeshInstance3Ds cost less than the churn.
    /// </summary>
    private void EnsureNodes(List<MultiMeshInstance3D> into, string prefix, int want)
    {
        while (into.Count < want)
        {
            var n = new MultiMeshInstance3D
            {
                Name = $"{prefix}{into.Count}",
                // Material is assigned by the caller, which knows the grid.
                // Scale, position and yaw match the shard form exactly, so switching a
                // tile between Earth and anything else does not move the footprint.
                Position = AuraMesh != null ? AuraMesh.Position : Vector3.Zero,
                Scale = new Vector3(FormScale, FormScale, FormScale),
                Rotation = new Vector3(0f, Mathf.DegToRad(FormYawDegrees), 0f),
            };
            AddChild(n);
            into.Add(n);
        }
    }

    /// <summary>Points a node list at a set of MultiMeshes, hiding any spares.</summary>
    private static void Bind(List<MultiMeshInstance3D> nodes, MultiMesh[] meshes, Material mat, Aabb aabb)
    {
        for (int i = 0; i < nodes.Count; i++)
        {
            bool used = meshes != null && i < meshes.Length;
            nodes[i].Visible = used;
            if (!used) continue;
            nodes[i].MaterialOverride = mat;
            nodes[i].Multimesh = meshes[i];
            nodes[i].CustomAabb = aabb;
        }
    }

    /// <summary>
    /// Drives the uprooting: strain, break through, bed down.
    ///
    /// Three beats, and the first one is the reason the others land. A stone that simply
    /// travels upward reads as a lift; one that shudders in place first, fails to move,
    /// and THEN breaks through reads as something heavy being forced. The overshoot and
    /// the settle are the follow-through: it throws itself clear and drops back into
    /// its own spoil.
    ///
    /// Each stone has its own delay, burial depth and shudder phase, so they break the
    /// surface raggedly. Seven arriving in unison reads as a platform being raised.
    ///
    /// Rewrites per-instance transforms rather than animating the node or the shader:
    /// the node moves everything together (no stagger) and the shader is shared with
    /// every rock on the map (must not become imbuement-aware). At seven instances for
    /// under a second, the cost is not worth a cleverer answer.
    /// </summary>
    private void ApplyRise(float t)
    {
        if (_scatter?.Stones == null) return;

        for (int v = 0; v < _scatter.Stones.Length && v < _rockNodes.Count; v++)
        {
            var mm = _scatter.Stones[v];
            var final = _scatter.StoneFinal[v];
            var rise = _scatter.StoneRise[v];

            for (int i = 0; i < final.Length; i++)
            {
                // rise[i] = (burial depth, start delay).
                float u = Mathf.Clamp(t - rise[i].Y, 0f, 1f);
                float depth = rise[i].X;

                // Per-stone shudder phase. Derived from the indices rather than stored:
                // it only has to be DIFFERENT per stone, not meaningful, and a third
                // component on rise[] would be a data change for no information.
                float phase = i * 1.7f + v * 0.9f;

                float yOff, shake;
                float rumble = Mathf.Clamp(RockRumbleFraction, 0.01f, 0.9f);

                if (u < rumble)
                {
                    // STRAINING. Still fully buried; only the shudder grows, and it
                    // grows quadratically so the build is felt rather than announced.
                    float k = u / rumble;
                    yOff = -depth;
                    shake = RockRumbleAmount * k * k;
                }
                else
                {
                    // BREAKING THROUGH. Quartic ease-out, sharper than the cubic this
                    // replaces, which read as a lift. Then an overshoot that peaks just
                    // after the surface and decays to nothing, so the stone throws
                    // itself clear and beds back down into its own spoil.
                    float w = (u - rumble) / (1f - rumble);
                    float e = 1f - Mathf.Pow(1f - w, 4f);
                    float pop = Mathf.Sin(Mathf.Pi * w) * (1f - w);
                    yOff = -depth * (1f - e) + depth * RockOvershoot * pop;
                    // A trailing shudder, so it does not snap from shaking to still.
                    shake = RockRumbleAmount * (1f - w) * 0.35f;
                }

                var tf = final[i];
                tf.Origin = new Vector3(
                    tf.Origin.X + Mathf.Sin(t * RockRumbleFreq + phase) * shake,
                    tf.Origin.Y + yOff + Mathf.Sin(t * RockRumbleFreq * 1.7f + phase) * shake * 0.4f,
                    tf.Origin.Z + Mathf.Cos(t * RockRumbleFreq * 0.9f + phase) * shake);
                mm.SetInstanceTransform(i, tf);
            }
        }

        if (_scatter.Debris == null) return;

        for (int v = 0; v < _scatter.Debris.Length && v < _debrisNodes.Count; v++)
        {
            var mm = _scatter.Debris[v];
            var final = _scatter.DebrisFinal[v];
            var delay = _scatter.DebrisDelay[v];

            for (int i = 0; i < final.Length; i++)
            {
                float local = Mathf.Clamp((t - delay[i]) * 2.5f, 0f, 1f);
                float e = 1f - Mathf.Pow(1f - local, 2f);
                var tf = final[i];
                // Scale in from nothing. A clod that slides up looks like a small rock
                // rising; one that appears looks like soil being thrown.
                tf.Basis = tf.Basis.Scaled(new Vector3(e, e, e));
                mm.SetInstanceTransform(i, tf);
            }
        }
    }

    /// <summary>Re-anchors the glyph plane after <see cref="_glyphLift"/> changes.</summary>
    private void RefreshGlyphHeight()
    {
        if (GlyphMesh == null || !AutoFitToTile) return;
        var pos = GlyphMesh.Position;
        pos.Y = _tileTopY + GlyphBaseHeight + _glyphLift;
        GlyphMesh.Position = pos;
    }

    /// <summary>
    /// Swaps the glyph plane over to a rune drawn by the SAME renderer that draws an
    /// Enchanter's spell sigil (see ElementRunes.cs). Falls back silently to the shader's
    /// analytic symbol.
    ///
    /// The fallback is the point, not politeness: element is targetable, consumable
    /// gameplay state, and a tile showing NO element marker is a gameplay bug, not a
    /// cosmetic one. Same lesson as HexTile.ShowFallbackMarker. So `use_rune_tex` is
    /// cleared FIRST and only set once a non-null texture is actually in hand. If
    /// the player re-imbues the tile while a bake is in flight, the stale callback is
    /// dropped rather than painting the wrong element's rune.
    /// </summary>
    private void RequestInkedRune(TileElementType element)
    {
        if (_glyphMaterial == null) return;

        // Show the correct-but-plain symbol rather than the previous element's pretty
        // one. Two frames of the wrong SHAPE is a lie; two frames of the plain shape
        // is only ugly.
        _glyphMaterial.SetShaderParameter("use_rune_tex", false);

        if (!UseInkedRunes) return;

        var tex = GlyphCipherTexture.Instance;
        if (tex == null) return;

        var glyph = ElementRunes.Build(element);
        if (glyph == null) return;

        tex.RequestRuneAsync(element.ToString(), glyph, RunePixels, CipherLod.Tile, true, t =>
        {
            if (t == null) return;                       // bake failed -> keep the fallback
            if (_current != element) return;             // tile was re-imbued mid-bake
            if (_glyphMaterial == null) return;
            _glyphMaterial.SetShaderParameter("rune_tex", t);
            _glyphMaterial.SetShaderParameter("use_rune_tex", true);
        });
    }

    /// <summary>Currently displayed elemental imbuement. <see cref="TileElementType.None"/> when the overlay is hidden.</summary>
    public TileElementType CurrentElement => _current;
}