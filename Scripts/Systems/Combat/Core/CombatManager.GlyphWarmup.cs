using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// CombatManager.GlyphWarmup.cs  (partial of CombatManager)
//
// Purpose:  Removes the hitch on the first glyph cast. Two costs used to
//           land at the moment of the cast:
//             1. The decal mask bake (SubViewport render, two-frame wait,
//                GPU readback, CPU mipmaps). Cached per card half, so it
//                hit once per distinct glyph card.
//             2. Pipeline compilation for glyph_sigil, glyph_aura and the
//                fallback Label3D the first time each is drawn. Godot
//                compiles on first draw, not on load, and the sigil
//                shader is large, so this was the bulk of the freeze.
//           Both now run while the board is being dealt.
// ============================================================
public partial class CombatManager
{
    private bool _glyphPrewarmed;

    private void PrewarmGlyphs()
    {
        if (_glyphPrewarmed || GlyphCipherTexture.Instance == null)
            return;

        var halves = new List<(string id, string half)>();
        var seen = new HashSet<string>();

        void Take(Card c, CardHalf h, string side)
        {
            if (c == null || h == null || string.IsNullOrEmpty(c.BlueprintId))
                return;
            if (!GlyphManager.HalfPreparesGlyph(h))
                return;
            if (seen.Add(c.BlueprintId + "|" + side))
                halves.Add((c.BlueprintId, side));
        }

        void Collect(List<Card> pile)
        {
            if (pile == null) return;
            foreach (var c in pile)
            {
                Take(c, c?.TopHalf, "top");
                Take(c, c?.BottomHalf, "bottom");
            }
        }

        foreach (var u in playerUnits)
        {
            var d = u?.DeckData;
            if (d == null) continue;
            Collect(d.DrawPile);
            Collect(d.Hand);
            Collect(d.DiscardPile);
        }

        if (halves.Count == 0)
            return;                       // no glyph cards in play: nothing to warm

        _glyphPrewarmed = true;
        GD.Print($"[GlyphWarmup] prewarming {halves.Count} glyph mask(s) and shaders");
        GlyphCipherTexture.Instance.PrewarmMasks(halves, HexTile.GlyphDecalPixels);
        WarmGlyphShaders();
    }

    /// <summary>
    /// Draws one tiny sigil quad, aura cylinder and ✦ label just in front of the
    /// camera for a few frames, which forces Godot to build their render
    /// pipelines now. Same mesh types and render modes as HexTile uses, so the
    /// pipelines match. They are a few millimetres across: invisible in practice.
    /// </summary>
    private async void WarmGlyphShaders()
    {
        Node3D root = null;
        try
        {
            var cam = GetViewport()?.GetCamera3D();
            if (cam == null)
                return;

            root = new Node3D { Name = "GlyphShaderWarmup", Position = new Vector3(0f, 0f, -1.5f) };

            var sigil = GD.Load<Shader>(HexTile.GlyphDecalShaderPath);
            if (sigil != null)
            {
                var img = Image.CreateEmpty(4, 4, true, Image.Format.Rgba8);
                img.Fill(new Color(1, 1, 1, 1));
                var mat = new ShaderMaterial { Shader = sigil };
                mat.SetShaderParameter("mask_tex", ImageTexture.CreateFromImage(img));
                mat.SetShaderParameter("progress", 1f);
                root.AddChild(new MeshInstance3D
                {
                    Mesh = new QuadMesh { Size = new Vector2(0.004f, 0.004f) },
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                    MaterialOverride = mat,
                });
            }

            var auraShader = HexTile.GlyphAuraEnabled ? GD.Load<Shader>(HexTile.GlyphAuraShaderPath) : null;
            if (auraShader != null)
            {
                var amat = new ShaderMaterial { Shader = auraShader };
                amat.SetShaderParameter("progress", 1f);
                root.AddChild(new MeshInstance3D
                {
                    Mesh = new CylinderMesh
                    {
                        TopRadius = 0.002f, BottomRadius = 0.002f, Height = 0.004f,
                        RadialSegments = 32, Rings = 1, CapTop = false, CapBottom = false,
                    },
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                    MaterialOverride = amat,
                });
            }

            root.AddChild(new Label3D
            {
                Text = "✦",
                FontSize = UITheme.Label3DGlyph,
                Modulate = UITheme.TileGlyph,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                PixelSize = 0.00005f,
            });

            cam.AddChild(root);

            for (int i = 0; i < 4; i++)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                if (!IsInstanceValid(root)) return;
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[GlyphWarmup] shader warmup failed: {ex.Message}");
        }
        finally
        {
            if (root != null && IsInstanceValid(root))
                root.QueueFree();
        }
    }
}
