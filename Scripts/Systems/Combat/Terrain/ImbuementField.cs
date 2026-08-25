using Godot;
using System.Collections.Generic;

// ============================================================
// ImbuementField.cs
//
// Purpose:        A world-space texture recording where each elemental
//                 imbuement is on the board, so the TERRAIN can respond
//                 to it: snow settling on grass, fire scorching it away.
// Layer:          Terrain
// Collaborators:  HexTile.cs (stamps on SetElement),
//                 painterly_grass.gdshader (samples it in vertex),
//                 HexGridManager.PainterlyGrass.cs (attaches it)
// See:            docs/imbuement_painterly_redesign_v1.md §3.1, §6.2
// ============================================================
//
// WHY A TEXTURE AND NOT SOMETHING SIMPLER
//
// The redesign doc originally costed "frozen grass stops swaying" at *one
// uniform*. That was wrong three separate ways, and each one on its own kills
// the cheap version:
//
//   1. `INSTANCE_CUSTOM.r` is already taken. painterly_grass reads it for
//      `stiffness_from_instance_height`.
//   2. `mm.UseCustomData` is only enabled when instance heights are written, so
//      any design leaning on `.g`/`.b` silently no-ops on some maps.
//   3. Grass is chunked at `GrassChunkTiles = 3`. NINE tiles share one
//      MultiMesh, so nothing per-chunk and nothing on the material can express
//      per-tile state.
//
// A world-space lookup dodges all three: the grass asks "what is happening at
// MY world position", which is a question the chunking cannot interfere with.
// It also pays for itself, because terrain_splat, painterly_flower and
// painterly_canopy can all read the same texture without any new plumbing.
//
// ── Channels ────────────────────────────────────────────────────────────
//   R  SNOW    (Frost)          settles on blade tips, stiffens them against wind
//   G  BARE     (Fire, Earth)    darkens and SHORTENS the blades; high values kill them
//   B  WET      (Water)          darkens and saturates
//   A  WITHER   (Shadow)         desaturates and crushes value
//
// Lightning, Air and Arcane deliberately write NOTHING. Not an oversight: a
// ground effect for every element turns the board into a rash, and those three
// have no obvious thing they do to grass.
//
// ── G is shared, and that was a decision ────────────────────────────────
//
// Fire and Earth both write G. The channel is really "how bare is this ground",
// and both elements make it barer: fire by burning the grass off, earth by
// heaving rock and spoil through it. They are separated by STRENGTH (see
// StrengthOf): fire drives it to 1.0 and reads as char; earth stops at ~0.55,
// which lands roughly halfway between turf and char and reads as churned mud.
//
// The cost, stated so nobody rediscovers it the hard way: **the field can no
// longer tell fire from earth.** The moment those two want to look genuinely
// different on the ground (not just different in degree), this has to be
// unpacked, and the two ways out are (a) a second texture, or (b) repacking to
// element-id + strength, which frees two channels but breaks the soft blended
// edges between neighbouring tiles, and that softness is the thing that makes a
// drift read as weather rather than as a grid.
//
// It was taken because a shared channel that degrades gracefully beat spending
// the last of the packing budget on a distinction nothing needs yet.
//
// ── Why blur is a feature ───────────────────────────────────────────────
// Each tile stamps a radial falloff, and the shader samples with bilinear
// filtering. Snowdrifts and scorch marks that bleed a little past the hex
// boundary read as weather; ones that stop exactly on the tile edge read as a
// grid. The gameplay-critical "which tile is imbued" question is answered by
// the FORM standing on the tile, not by this, which is what buys this the
// licence to be soft.
// ============================================================

/// <summary>
/// The board-wide imbuement lookup. One 512² RGBA texture covering
/// <see cref="Extent"/> world units, updated when a tile's element changes.
/// </summary>
public static class ImbuementField
{
    /// <summary>Edge resolution. 512 over the default extent is ~5 texels per tile, enough for a soft edge and cheap enough to rewrite on a whim.</summary>
    public const int Pixels = 512;

    /// <summary>World units spanned by the field, centred on <see cref="Origin"/>. Raise for a larger board; the field silently ignores tiles outside it.</summary>
    public static float Extent = 96f;

    /// <summary>World XZ centre of the field.</summary>
    public static Vector2 Origin = Vector2.Zero;

    /// <summary>
    /// Radius of one hex in world units. Sets how wide a single tile's stamp is.
    ///
    /// SET FROM THE GRID by <see cref="Attach"/>, not hardcoded. The 1.0 here is only a
    /// last resort: this project runs at HexRadius 1.325, so a hardcoded 1.0 stamped a
    /// disc 25% too small and the effect stopped short of the tile edges. It is the same
    /// invented-constant mistake ImbuementRocks made with the boulder scale.
    /// </summary>
    public static float TileRadius = 1.0f;

    private static bool _announced;

    private static Image _img;
    private static ImageTexture _tex;

    // Keyed by tile instance id so a tile can be re-stamped or cleared without
    // the caller tracking anything.
    private static readonly Dictionary<ulong, Stamp> _stamps = new();

    private readonly struct Stamp
    {
        public readonly Vector2 Xz;
        public readonly TileElementType Element;
        public Stamp(Vector2 xz, TileElementType e) { Xz = xz; Element = e; }
    }

    /// <summary>The lookup texture. Created on first access; the same object for the session, so attaching once is enough.</summary>
    public static ImageTexture Texture
    {
        get
        {
            Ensure();
            return _tex;
        }
    }

    /// <summary>
    /// Wires the field into a material and switches its imbuement response on. Safe to call
    /// repeatedly and safe to call before any tile is imbued, since the texture object never
    /// changes, only its contents.
    /// </summary>
    public static void Attach(ShaderMaterial material, HexGridManager grid = null)
    {
        if (grid != null && grid.HexRadius > 0.0001f)
            TileRadius = grid.HexRadius;

        if (material == null) return;
        Ensure();

        // "Is the field even live?" should be answerable from the log rather than by
        // inspection. That is the same reason GlyphCipherTexture prints on _Ready. Every
        // number here has been wrong at least once.
        if (!_announced)
        {
            _announced = true;
            GD.Print($"[ImbuementField] Attached. {Pixels}px over {Extent} world units " +
                     $"({Extent / Pixels:F3} u/texel), tile radius {TileRadius:F3}, " +
                     $"stamp {StampRadius:F3}.");
        }
        material.SetShaderParameter("imbuement_field", _tex);
        material.SetShaderParameter("field_origin", Origin);
        material.SetShaderParameter("field_extent", Extent);
        material.SetShaderParameter("use_imbuement_field", true);
    }

    /// <summary>
    /// Records the element on one tile. Pass <see cref="TileElementType.None"/> to clear it.
    /// Cheap enough to call on every imbuement: only the changed tile's footprint is
    /// rewritten, plus whatever overlaps it.
    /// </summary>
    public static void SetTile(ulong tileId, Vector3 worldPos, TileElementType element)
    {
        Ensure();

        var xz = new Vector2(worldPos.X, worldPos.Z);

        if (_stamps.TryGetValue(tileId, out var old))
        {
            _stamps.Remove(tileId);
            Repaint(old.Xz);                 // erase where it used to be
        }

        if (element != TileElementType.None && ChannelOf(element) >= 0)
        {
            _stamps[tileId] = new Stamp(xz, element);
            Repaint(xz);
        }

        _tex.Update(_img);

        GD.Print($"[ImbuementField] {(element == TileElementType.None ? "cleared" : "stamped")} " +
                 $"({worldPos.X:F2}, {worldPos.Z:F2}) {element} -> channel {ChannelOf(element)} " +
                 $"at {StrengthOf(element):F2}. {_stamps.Count} live.");
    }

    /// <summary>Forgets every stamp and clears the field. Call when leaving combat.</summary>
    public static void Clear()
    {
        _stamps.Clear();
        if (_img == null) return;
        _img.Fill(new Color(0, 0, 0, 0));
        _tex.Update(_img);
    }

    // ── Internals ───────────────────────────────────────────────────

    /// <summary>R=snow, G=bare, B=wet, A=wither. -1 = this element does nothing to terrain.</summary>
    private static int ChannelOf(TileElementType e) => e switch
    {
        TileElementType.Frost  => 0,
        TileElementType.Fire   => 1,
        TileElementType.Earth  => 1,   // shares G with Fire (see the header)
        TileElementType.Water  => 2,
        TileElementType.Shadow => 3,
        _ => -1,
    };

    /// <summary>
    /// How hard an element drives its channel. This is what separates two elements
    /// sharing one: fire burns the grass off completely, earth only pushes rock and
    /// spoil through it, so earth stops well short of char.
    ///
    /// Tune Earth here rather than in the grass shader. The shader's scorch_* uniforms
    /// belong to Fire and moving them to make Earth read right would silently restyle
    /// every burnt tile on the map.
    /// </summary>
    private static float StrengthOf(TileElementType e) => e switch
    {
        TileElementType.Earth => 0.55f,
        _ => 1.0f,
    };

    private static void Ensure()
    {
        if (_img != null) return;
        _img = Image.CreateEmpty(Pixels, Pixels, false, Image.Format.Rgba8);
        _img.Fill(new Color(0, 0, 0, 0));
        _tex = ImageTexture.CreateFromImage(_img);
    }

    /// <summary>Stamp radius in world units. Slightly over one hex so neighbours bleed into each other.</summary>
    private static float StampRadius => TileRadius * 1.15f;

    /// <summary>
    /// Rewrites every texel within one stamp radius of <paramref name="centre"/> from scratch,
    /// summing every recorded tile that reaches it.
    ///
    /// Rewriting rather than subtracting is deliberate: an incremental erase has to model
    /// exactly how the neighbours' falloffs overlapped, and gets it subtly wrong the first
    /// time two elements touch. Recomputing a ~30x30 texel patch from the authoritative
    /// dictionary cannot drift.
    /// </summary>
    private static void Repaint(Vector2 centre)
    {
        float px = Extent / Pixels;                        // world units per texel
        float r = StampRadius;

        int x0 = TexelX(centre.X - r), x1 = TexelX(centre.X + r);
        int y0 = TexelY(centre.Y - r), y1 = TexelY(centre.Y + r);

        for (int y = Mathf.Max(y0, 0); y <= Mathf.Min(y1, Pixels - 1); y++)
        {
            for (int x = Mathf.Max(x0, 0); x <= Mathf.Min(x1, Pixels - 1); x++)
            {
                var world = new Vector2(
                    Origin.X + (x + 0.5f) * px - Extent * 0.5f,
                    Origin.Y + (y + 0.5f) * px - Extent * 0.5f);

                var acc = new Color(0, 0, 0, 0);
                foreach (var s in _stamps.Values)
                {
                    float d = world.DistanceTo(s.Xz);
                    if (d >= r) continue;

                    // Flat-topped falloff: solid across the tile, soft only at the
                    // rim. A pure radial gradient leaves the tile centre as weak as
                    // its edge and the effect never reads.
                    float w = (1f - Mathf.SmoothStep(r * 0.55f, r, d)) * StrengthOf(s.Element);
                    if (w <= 0f) continue;

                    int c = ChannelOf(s.Element);
                    if (c == 0) acc.R = Mathf.Max(acc.R, w);
                    else if (c == 1) acc.G = Mathf.Max(acc.G, w);
                    else if (c == 2) acc.B = Mathf.Max(acc.B, w);
                    else if (c == 3) acc.A = Mathf.Max(acc.A, w);
                }
                _img.SetPixel(x, y, acc);
            }
        }
    }

    private static int TexelX(float worldX)
        => Mathf.FloorToInt((worldX - Origin.X + Extent * 0.5f) / (Extent / Pixels));

    private static int TexelY(float worldZ)
        => Mathf.FloorToInt((worldZ - Origin.Y + Extent * 0.5f) / (Extent / Pixels));
}
