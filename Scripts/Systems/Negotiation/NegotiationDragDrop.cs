using Godot;
using System;

// ============================================================
// NegotiationDragDrop.cs
//
// Purpose:        The physical-token widgets (Phase 4):
//                 NegotiationTokenChip renders a leverage (or
//                 NPC-pool) token from Assets/UI/Tokens/*.png
//                 with a ×N count tag, and CLICKING it spends it
//                 toward the currently selected clause / action.
//                 (Drag-and-drop plumbing is retained on the chip
//                 and NegotiationDropZone for a future pass, but
//                 click is the primary interaction.)
// Layer:          UI
// Collaborators:  NegotiationManager.cs (creates chips, supplies
//                 Clicked handlers + CanDrag gate),
//                 Assets/UI/Tokens/{name}.png (placeholder art —
//                 swap the PNGs, no code changes)
// ============================================================

/// <summary>A leverage-token chip. Shows Assets/UI/Tokens/{name}.png (falls
/// back to a colored disc with an initial) plus a ×N tag. Interactive chips
/// emit <see cref="Clicked"/> on left-click; NPC-pool chips set
/// <see cref="Interactive"/> = false and are display-only.</summary>
public partial class NegotiationTokenChip : PanelContainer
{
    private const string ART_DIR = "res://Assets/UI/Tokens/";

    public LeverageToken Token;
    /// <summary>Art file name override (e.g. "resolve" for NPC-pool chips).
    /// Empty = use the Token's name.</summary>
    public string ArtOverride = "";
    public int Count = 1;
    /// <summary>Chip diameter in pixels (66 for the player rack, 44 for the
    /// NPC pool).</summary>
    public int SizePx = 66;
    /// <summary>False for display-only chips (NPC pool, drag previews).</summary>
    public bool Interactive = true;
    /// <summary>Manager-supplied gate for click/drag — e.g. table resolved.</summary>
    public Func<bool> CanDrag;
    /// <summary>Primary interaction: click to spend.</summary>
    public event Action Clicked;

    public override void _Ready()
    {
        CustomMinimumSize = new Vector2(SizePx + 2, SizePx + 2);
        AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
        if (Interactive)
        {
            MouseDefaultCursorShape = CursorShape.PointingHand;
            // Receive clicks here (PanelContainer inherits Container's PASS
            // default, which would let them bubble past the chip).
            MouseFilter = MouseFilterEnum.Stop;
        }

        var holder = new Control
        {
            CustomMinimumSize = new Vector2(SizePx, SizePx),
            // A bare Control defaults to MOUSE_FILTER_STOP: without this
            // override the holder sits on top of the chip, swallows every
            // press, and _GuiInput/_GetDragData below never fire — which is
            // exactly the "can't play actions in spoken-lines mode" bug.
            MouseFilter = MouseFilterEnum.Ignore,
        };
        AddChild(holder);

        string artName = string.IsNullOrEmpty(ArtOverride)
            ? Token.ToString().ToLowerInvariant()
            : ArtOverride;
        string path = $"{ART_DIR}{artName}.png";
        if (ResourceLoader.Exists(path))
        {
            holder.AddChild(new TextureRect
            {
                Texture = GD.Load<Texture2D>(path),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                AnchorRight = 1f,
                AnchorBottom = 1f,
                MouseFilter = MouseFilterEnum.Ignore,
            });
        }
        else
        {
            // Fallback disc + initial, so a missing PNG never breaks the table.
            var disc = new Panel { AnchorRight = 1f, AnchorBottom = 1f, MouseFilter = MouseFilterEnum.Ignore };
            disc.AddThemeStyleboxOverride("panel", new StyleBoxFlat
            {
                BgColor = UITheme.BgRaised,
                BorderColor = UITheme.Violet,
                BorderWidthTop = 2, BorderWidthBottom = 2,
                BorderWidthLeft = 2, BorderWidthRight = 2,
                CornerRadiusTopLeft = SizePx / 2, CornerRadiusTopRight = SizePx / 2,
                CornerRadiusBottomLeft = SizePx / 2, CornerRadiusBottomRight = SizePx / 2,
            });
            holder.AddChild(disc);
            var initial = new Label
            {
                Text = artName.Length > 0 ? artName[..1].ToUpperInvariant() : "?",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                AnchorRight = 1f,
                AnchorBottom = 1f,
                MouseFilter = MouseFilterEnum.Ignore,
            };
            initial.AddThemeFontSizeOverride("font_size", SizePx / 2 - 6);
            initial.AddThemeColorOverride("font_color", UITheme.NegotiationTitleColor);
            holder.AddChild(initial);
        }

        // ×N count tag, bottom-right.
        var tag = new Label
        {
            Text = $"×{Count}",
            AnchorLeft = 1f, AnchorTop = 1f, AnchorRight = 1f, AnchorBottom = 1f,
            OffsetLeft = -(int)(SizePx * 0.46f), OffsetTop = -(int)(SizePx * 0.34f),
            OffsetRight = -1, OffsetBottom = -1,
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        tag.AddThemeFontSizeOverride("font_size",
            SizePx >= 60 ? UITheme.NegotiationSmallFontSize : UITheme.NegotiationTinyFontSize);
        tag.AddThemeColorOverride("font_color", UITheme.NegotiationTitleColor);
        tag.AddThemeStyleboxOverride("normal", new StyleBoxFlat
        {
            BgColor = UITheme.BgDeep,
            BorderColor = UITheme.VioletDim,
            BorderWidthTop = 1, BorderWidthBottom = 1,
            BorderWidthLeft = 1, BorderWidthRight = 1,
            CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8,
            ContentMarginLeft = 4, ContentMarginRight = 4,
        });
        holder.AddChild(tag);
    }

    public override void _GuiInput(InputEvent e)
    {
        if (!Interactive) return;
        if (e is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
        {
            if (CanDrag != null && !CanDrag()) return;
            AcceptEvent();
            Clicked?.Invoke();
        }
    }

    // ── Drag plumbing (secondary; retained for a future pass) ────────────

    public override Variant _GetDragData(Vector2 atPosition)
    {
        if (!Interactive || (CanDrag != null && !CanDrag())) return default;
        SetDragPreview(new NegotiationTokenChip
        {
            Token = Token,
            ArtOverride = ArtOverride,
            Count = Count,
            SizePx = SizePx,
            Interactive = false,
            Modulate = new Color(1f, 1f, 1f, 0.85f),
        });
        return new Godot.Collections.Dictionary { { "negotiation_token", (int)Token } };
    }

    public static LeverageToken? ExtractToken(Variant data)
    {
        if (data.VariantType != Variant.Type.Dictionary) return null;
        var d = data.AsGodotDictionary();
        if (!d.ContainsKey("negotiation_token")) return null;
        return (LeverageToken)(int)d["negotiation_token"];
    }
}

/// <summary>Anything a token could be dropped ON. Unused by the current
/// click-first interaction; kept for a future drag pass.</summary>
public partial class NegotiationDropZone : PanelContainer
{
    public Func<LeverageToken, bool> CanDropToken;
    public Action<LeverageToken> OnTokenDropped;

    public override bool _CanDropData(Vector2 atPosition, Variant data)
    {
        var tok = NegotiationTokenChip.ExtractToken(data);
        return tok.HasValue && (CanDropToken?.Invoke(tok.Value) ?? false);
    }

    public override void _DropData(Vector2 atPosition, Variant data)
    {
        var tok = NegotiationTokenChip.ExtractToken(data);
        if (tok.HasValue) OnTokenDropped?.Invoke(tok.Value);
    }
}
