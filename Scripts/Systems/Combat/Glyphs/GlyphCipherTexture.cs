using Godot;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

// ============================================================
// GlyphCipherTexture.cs
//
// Purpose:        Bakes a GlyphCipherView into an ImageTexture and
//                 caches it per (card id, half, size, LOD), so the
//                 hex-tile decal is a single Sprite3D texture
//                 rather than a live Control redrawing every frame.
// Layer:          System (Combat)
// Collaborators:  GlyphCipherView.cs (the Control that is baked),
//                 GlyphCipherTags.cs (semantic extraction),
//                 HexTile.cs (consumer — the tile decal),
//                 CardDatabase.cs (blueprint lookup by id)
// See:            docs/glyph_cipher_spec_v1.md §10 — tile decal
// ============================================================
//
// Add as an autoload named "GlyphCipherTexture" (Project Settings ->
// Autoload) or as a child of the combat root before any tile asks for
// a glyph. Without an instance in the tree, RequestAsync degrades to
// a no-op and the caller keeps whatever placeholder it had.
//
// ============================================================

/// <summary>
/// Async bake-and-cache for cipher glyph textures. One instance in the tree; callers use
/// <see cref="RequestAsync"/> and get a callback when the texture is ready (usually the
/// same frame for a cache hit, two frames for a cold bake).
/// </summary>
public partial class GlyphCipherTexture : Node
{
    /// <summary>The live instance, set in <see cref="_Ready"/>. Null before the combat scene is up.</summary>
    public static GlyphCipherTexture Instance { get; private set; }

    private readonly Dictionary<string, ImageTexture> _cache = new();
    private readonly Dictionary<string, List<Action<ImageTexture>>> _pending = new();

    public override void _Ready()
    {
        Instance = this;
        ProcessMode = ProcessModeEnum.Always;
        GD.Print("[GlyphCipherTexture] Ready. Instance set: True");
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
        _cache.Clear();
        _pending.Clear();
    }

    private static string KeyOf(string cardId, string half, int px, CipherLod lod, bool dark)
        => $"{cardId}#{half}#{px}#{lod}#{(dark ? 'd' : 'l')}";

    /// <summary>Synchronous cache probe. Returns null on a miss; never bakes.</summary>
    public ImageTexture TryGet(string cardId, string half, int px, CipherLod lod, bool dark)
        => _cache.TryGetValue(KeyOf(cardId, half, px, lod, dark), out var t) ? t : null;

    /// <summary>
    /// Requests a baked glyph texture. <paramref name="onReady"/> is invoked immediately on a
    /// cache hit, otherwise after the bake completes. Concurrent requests for the same key
    /// share one bake. Safe to call from <c>_Ready</c>.
    /// </summary>
    public void RequestAsync(string cardId, string half, CardHalf data, int px,
                             CipherLod lod, bool dark, Action<ImageTexture> onReady)
    {
        if (onReady == null) return;
        if (string.IsNullOrEmpty(cardId) || data == null) { onReady(null); return; }

        px = Mathf.Clamp(px, 32, 1024);
        string key = KeyOf(cardId, half, px, lod, dark);

        if (_cache.TryGetValue(key, out var hit)) { onReady(hit); return; }

        if (_pending.TryGetValue(key, out var waiters)) { waiters.Add(onReady); return; }

        _pending[key] = new List<Action<ImageTexture>> { onReady };
        _ = BakeAsync(key, cardId, half, data, px, lod, dark);
    }

    /// <summary>Awaitable bake. Prefer <see cref="RequestAsync"/> from node code.</summary>
    public async Task<ImageTexture> BakeAsync(string key, string cardId, string half,
                                              CardHalf data, int px, CipherLod lod, bool dark)
    {
        ImageTexture result = null;
        SubViewport vp = null;
        try
        {
            var glyph = GlyphCipherTags.BuildFor(cardId, half, data);
            if (glyph == null) { Resolve(key, null); return null; }

            vp = new SubViewport
            {
                Size = new Vector2I(px, px),
                TransparentBg = true,
                Disable3D = true,
                RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
                RenderTargetClearMode = SubViewport.ClearMode.Always,
            };

            var view = new GlyphCipherView
            {
                Glyph = glyph,
                Lod = lod,
                DarkBackground = dark,
                Progress = 1f,
                Position = Vector2.Zero,
                Size = new Vector2(px, px),
            };
            vp.AddChild(view);
            AddChild(vp);

            // Two frames: one for the Control to lay out and queue its draw, one for
            // the viewport to actually resolve. One frame is enough on most drivers
            // and produces an empty texture on the rest — which shows up as an
            // invisible glyph that is very hard to diagnose later.
            await ToSignal(RenderingServer.Singleton, "frame_post_draw");
            await ToSignal(RenderingServer.Singleton, "frame_post_draw");

            var img = vp.GetTexture()?.GetImage();
            if (img != null && img.GetWidth() > 0)
            {
                // Mipmaps matter here: the tile decal lies flat on the ground and is
                // routinely viewed at a grazing angle, where an unmipped texture
                // aliases badly along every stroke. The sigil shader also samples with
                // filter_linear_mipmap, which silently falls back to the base level
                // without these.
                img.GenerateMipmaps();
                result = ImageTexture.CreateFromImage(img);
            }
            else
                GD.PrintErr($"[GlyphCipherTexture] bake produced no image for {key}");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[GlyphCipherTexture] bake failed for {key}: {ex.Message}");
        }
        finally
        {
            if (IsInstanceValid(vp)) vp.QueueFree();
        }

        if (result != null) _cache[key] = result;
        Resolve(key, result);
        return result;
    }

    private void Resolve(string key, ImageTexture tex)
    {
        if (!_pending.TryGetValue(key, out var waiters)) return;
        _pending.Remove(key);
        foreach (var w in waiters)
        {
            try { w(tex); }
            catch (Exception ex) { GD.PrintErr($"[GlyphCipherTexture] callback threw for {key}: {ex.Message}"); }
        }
    }

    /// <summary>
    /// Convenience for board code that only knows a blueprint id: looks the card up in
    /// <see cref="CardDatabase"/> and bakes the requested half. Uses the blueprint id
    /// path of <c>GetByName</c>, never a display name.
    /// </summary>
    public void RequestForBlueprint(string blueprintId, string half, int px,
                                    CipherLod lod, bool dark, Action<ImageTexture> onReady)
    {
        var bp = CardDatabase.GetByName(blueprintId);
        var data = half == "bottom" ? bp?.Prebuilt?.BottomHalf : bp?.Prebuilt?.TopHalf;
        if (data == null) { onReady?.Invoke(null); return; }
        RequestAsync(blueprintId, half, data, px, lod, dark, onReady);
    }

    /// <summary>Drops every cached texture. Call on a resolution change or when leaving combat for a long session.</summary>
    public void ClearCache() => _cache.Clear();

    /// <summary>Number of cached textures. Diagnostic.</summary>
    public int CachedCount => _cache.Count;
}
