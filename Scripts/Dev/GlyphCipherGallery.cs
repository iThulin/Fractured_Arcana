using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// GlyphCipherGallery.cs
//
// Purpose:        Dev overlay that renders every Enchanter spell
//                 half through the real GlyphCipherView._Draw
//                 path, in a grid, so the in-engine result can be
//                 compared side by side with the reference contact
//                 sheet in docs/glyph_cipher_sheet.png.
// Layer:          Dev
// Collaborators:  GlyphCipherView.cs (the renderer under test),
//                 GlyphCipherTags.cs, CardDatabase.cs,
//                 GameBootstrap.cs (F11 toggle)
// See:            docs/glyph_cipher_integration_v2.md
// ============================================================
//
// This exists because the contact sheet was rendered by a Python/SVG
// reference renderer, NOT by this codebase. The stroke GENERATOR is
// mirrored bit-for-bit and the self-test proves it; the RENDERER is a
// second, independent implementation against a different drawing API.
// The only way to know the two agree is to look at both.
//
// Press F11 in a debug build. Escape closes.
//
// ============================================================

/// <summary>
/// Full-screen dev gallery of every Enchanter glyph, drawn through the shipping
/// renderer. Created on demand by <c>GameBootstrap</c>; never present in a release build.
/// </summary>
public partial class GlyphCipherGallery : CanvasLayer
{
    private const int Columns = 6;
    private const int CellSize = 150;

    private readonly List<GlyphCipherView> _views = new();
    private readonly List<Label> _captions = new();
    private ColorRect _backdrop;
    private Label _status;
    private CipherLod _lod = CipherLod.Card;
    private bool _dark;
    private float _replay = -1f;

    /// <summary>Creates the gallery under <paramref name="parent"/>, or frees the existing one.</summary>
    public static void Toggle(Node parent)
    {
        var existing = parent.GetNodeOrNull<GlyphCipherGallery>("GlyphCipherGallery");
        if (existing != null) { existing.QueueFree(); return; }

        var g = new GlyphCipherGallery { Name = "GlyphCipherGallery", Layer = 128 };
        parent.AddChild(g);
    }

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;

        _backdrop = new ColorRect { Color = UITheme.CipherPaper, MouseFilter = Control.MouseFilterEnum.Stop };
        _backdrop.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        AddChild(_backdrop);

        var scroll = new ScrollContainer();
        scroll.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        scroll.OffsetTop = 56;
        AddChild(scroll);

        var grid = new GridContainer { Columns = Columns };
        grid.AddThemeConstantOverride("h_separation", 8);
        grid.AddThemeConstantOverride("v_separation", 8);
        grid.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        scroll.AddChild(grid);

        _status = new Label { Position = new Vector2(16, 14) };
        _status.AddThemeColorOverride("font_color", UITheme.CipherInk);
        AddChild(_status);

        int built = 0, failed = 0;
        foreach (var bp in CardDatabase.Blueprints)
        {
            if (bp.School != CardSchool.Enchanter) continue;
            foreach (var (half, data) in new[] { ("top", bp.Prebuilt?.TopHalf), ("bottom", bp.Prebuilt?.BottomHalf) })
            {
                if (data == null) continue;

                var cell = new VBoxContainer { CustomMinimumSize = new Vector2(CellSize, CellSize + 40) };

                var view = new GlyphCipherView
                {
                    CustomMinimumSize = new Vector2(CellSize, CellSize),
                    Lod = _lod,
                    PaperColor = UITheme.CipherPaper,
                };
                if (view.SetSpell(bp.Id, half, data)) built++; else failed++;
                cell.AddChild(view);
                _views.Add(view);

                var caption = new Label
                {
                    Text = $"{data.Name}\n{DescribeSemantics(view.Glyph)}",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    AutowrapMode = TextServer.AutowrapMode.WordSmart,
                };
                caption.AddThemeFontSizeOverride("font_size", 11);
                caption.AddThemeColorOverride("font_color", UITheme.CipherInk);
                cell.AddChild(caption);
                _captions.Add(caption);

                grid.AddChild(cell);
            }
        }

        _status.Text = $"Glyph cipher gallery — {built} built, {failed} failed.   " +
                       "[L] LOD   [D] dark board   [R] replay draw-on   [Esc] close";
        if (built == 0)
            _status.Text = "Glyph cipher gallery — NO ENCHANTER BLUEPRINTS. " +
                           "CardDatabase is empty; is GameBootstrap running?";
    }

    private static string DescribeSemantics(CipherGlyph g)
    {
        if (g == null) return "(failed)";
        var parts = new List<string>();
        foreach (var v in GlyphCipher.VerbRingOrder)
            if ((g.Verbs & v) != 0) parts.Add(v.ToString().ToUpperInvariant());
        string verbs = parts.Count > 0 ? string.Join("+", parts) : "—";
        return $"{g.Target.ToString().ToUpperInvariant()} · {verbs}   {g.ArmCount}×{g.DeepestArm}";
    }

    public override void _Process(double delta)
    {
        if (_replay < 0f) return;
        _replay += (float)delta / 0.4f;                 // the spec's ~0.4s prepare animation
        float p = Mathf.Min(1f, _replay);
        foreach (var v in _views) v.Progress = p;
        if (p >= 1f) _replay = -1f;
    }

    public override void _UnhandledKeyInput(InputEvent e)
    {
        if (e is not InputEventKey k || !k.Pressed || k.Echo) return;

        switch (k.Keycode)
        {
            case Key.Escape:
                QueueFree();
                GetViewport().SetInputAsHandled();
                break;

            case Key.L:
                _lod = _lod switch
                {
                    CipherLod.Card => CipherLod.Tile,
                    CipherLod.Tile => CipherLod.Inspection,
                    _ => CipherLod.Card
                };
                foreach (var v in _views) v.Lod = _lod;
                Announce($"LOD: {_lod}");
                GetViewport().SetInputAsHandled();
                break;

            case Key.D:
                _dark = !_dark;
                Color paper = _dark ? new Color(0.090f, 0.075f, 0.071f) : UITheme.CipherPaper;
                _backdrop.Color = paper;
                foreach (var v in _views) { v.DarkBackground = _dark; v.PaperColor = paper; }
                foreach (var c in _captions)
                    c.AddThemeColorOverride("font_color", _dark ? UITheme.CipherInkLight : UITheme.CipherInk);
                _status.AddThemeColorOverride("font_color", _dark ? UITheme.CipherInkLight : UITheme.CipherInk);
                Announce(_dark ? "dark board" : "card stock");
                GetViewport().SetInputAsHandled();
                break;

            case Key.R:
                foreach (var v in _views) v.Progress = 0f;
                _replay = 0f;
                Announce("replaying draw-on");
                GetViewport().SetInputAsHandled();
                break;
        }
    }

    private void Announce(string what)
    {
        int i = _status.Text.IndexOf("   [", StringComparison.Ordinal);
        string head = i > 0 ? _status.Text[..i] : _status.Text;
        _status.Text = $"{head}   {what}   [L] LOD   [D] dark board   [R] replay draw-on   [Esc] close";
    }
}
