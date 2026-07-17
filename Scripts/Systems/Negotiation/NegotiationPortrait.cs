using Godot;

// ============================================================
// NegotiationPortrait.cs
//
// Purpose:        Phase 4 (art pass): the NPC portrait widget.
//                 Displays a per-archetype, per-stance portrait
//                 texture; the stance IS game-state information
//                 (Module A), so this widget is gameplay UI, not
//                 decoration. Falls back gracefully through
//                 {archetype}_{stance}.png → {archetype}_base.png
//                 → a styled placeholder, so the system ships
//                 before any art exists and lights up as PNGs
//                 are dropped into the folder.
// Layer:          UI
// Collaborators:  NegotiationManager.cs (owner; forwards
//                 OnStanceChanged / OnTensionChanged),
//                 UITheme.cs (colors)
// Art contract:   res://Assets/Portraits/Negotiation/
//                   {archetype}_{stance}.png   e.g. merchant_eager.png
//                   {archetype}_base.png       neutral fallback
//                 archetype ∈ merchant, commander, scholar,
//                             opportunist, idealist, survivor
//                 stance    ∈ eager, guarded, wavering,
//                             irritated, expansive
//                 Square images; ~512×512 recommended. 30 stance
//                 portraits + 6 bases total for full coverage.
// See:            negotiation_redesign_v1.md §5.2, Phase 4
// ============================================================

/// <summary>Portrait widget for the negotiation table. Swaps textures per
/// stance with a short fade, tints its ring by tension zone, and renders a
/// legible placeholder for any archetype/stance art that doesn't exist yet.</summary>
public partial class NegotiationPortrait : Control
{
    private const string ART_DIR = "res://Assets/Portraits/Negotiation/";

    private NpcArchetypeType _archetype = NpcArchetypeType.Merchant;
    private NpcStance _stance = NpcStance.Guarded;
    private TensionZone _zone = TensionZone.Strained;

    private Panel _ring;                 // zone-tinted circular frame
    private TextureRect _texRect;        // the art, when it exists
    private Label _placeholderInitial;   // archetype initial, when it doesn't
    private Label _placeholderStance;    // stance word under the initial
    private StyleBoxFlat _ringStyle;
    private Tween _fadeTween;

    public override void _Ready()
    {
        CustomMinimumSize = new Vector2(150, 150);

        _ringStyle = new StyleBoxFlat
        {
            BgColor = UITheme.BgRaised,
            BorderColor = UITheme.NegotiationTitleColor,
            BorderWidthTop = 3, BorderWidthBottom = 3,
            BorderWidthLeft = 3, BorderWidthRight = 3,
            CornerRadiusTopLeft = 75, CornerRadiusTopRight = 75,
            CornerRadiusBottomLeft = 75, CornerRadiusBottomRight = 75,
        };
        _ring = new Panel { AnchorRight = 1f, AnchorBottom = 1f };
        _ring.AddThemeStyleboxOverride("panel", _ringStyle);
        AddChild(_ring);

        _texRect = new TextureRect
        {
            AnchorRight = 1f, AnchorBottom = 1f,
            OffsetLeft = 6, OffsetTop = 6, OffsetRight = -6, OffsetBottom = -6,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            Visible = false,
        };
        AddChild(_texRect);

        var placeholderBox = new VBoxContainer
        {
            AnchorRight = 1f, AnchorBottom = 1f,
            Alignment = BoxContainer.AlignmentMode.Center,
        };
        AddChild(placeholderBox);

        _placeholderInitial = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        _placeholderInitial.AddThemeFontSizeOverride("font_size", 44);
        placeholderBox.AddChild(_placeholderInitial);

        _placeholderStance = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        _placeholderStance.AddThemeFontSizeOverride("font_size", UITheme.NegotiationTinyFontSize);
        _placeholderStance.AddThemeColorOverride("font_color", UITheme.NegotiationNpcColor);
        placeholderBox.AddChild(_placeholderStance);

        Refresh(instant: true);
    }

    /// <summary>Set the fixed identity for this table. Call once at open.</summary>
    public void Setup(NpcArchetypeType archetype)
    {
        _archetype = archetype;
        if (IsInsideTree()) Refresh(instant: true);
    }

    /// <summary>Module A hook: the per-round mood changed — swap expression.</summary>
    public void SetStance(NpcStance stance)
    {
        if (_stance == stance) return;
        _stance = stance;
        Refresh(instant: false);
    }

    /// <summary>Tension governor hook: tint the ring by zone.</summary>
    public void SetZone(TensionZone zone)
    {
        _zone = zone;
        ApplyRing();
    }

    // ── Internals ────────────────────────────────────────────────────────

    private void Refresh(bool instant)
    {
        var tex = ResolveTexture(_archetype, _stance);

        if (tex != null)
        {
            _texRect.Visible = true;
            _placeholderInitial.Visible = false;
            _placeholderStance.Visible = false;

            if (instant)
            {
                _texRect.Texture = tex;
                _texRect.Modulate = Colors.White;
            }
            else
            {
                // Quick dip-and-swap so the expression change reads as a beat.
                _fadeTween?.Kill();
                _fadeTween = CreateTween();
                _fadeTween.TweenProperty(_texRect, "modulate:a", 0.25f, 0.10f);
                _fadeTween.TweenCallback(Callable.From(() => _texRect.Texture = tex));
                _fadeTween.TweenProperty(_texRect, "modulate:a", 1f, 0.15f);
            }
        }
        else
        {
            _texRect.Visible = false;
            _placeholderInitial.Visible = true;
            _placeholderStance.Visible = true;
            _placeholderInitial.Text = _archetype.ToString()[..1];
            _placeholderInitial.AddThemeColorOverride("font_color", ArchetypeColor(_archetype));
            _placeholderStance.Text = _stance.ToString().ToUpperInvariant();
        }

        ApplyRing();
    }

    private void ApplyRing()
    {
        if (_ringStyle == null) return;
        _ringStyle.BorderColor = _zone switch
        {
            TensionZone.Cordial => UITheme.TensionCordial,
            TensionZone.Hostile => UITheme.TensionHostile,
            _                   => UITheme.NegotiationTitleColor,
        };
    }

    /// <summary>{archetype}_{stance}.png → {archetype}_base.png → null.
    /// Results cached per path; misses cached too (Exists is a disk probe).</summary>
    private static readonly System.Collections.Generic.Dictionary<string, Texture2D> _texCache = new();

    private static Texture2D ResolveTexture(NpcArchetypeType archetype, NpcStance stance)
    {
        string arch = archetype.ToString().ToLowerInvariant();
        foreach (var candidate in new[]
        {
            $"{ART_DIR}{arch}_{stance.ToString().ToLowerInvariant()}.png",
            $"{ART_DIR}{arch}_base.png",
        })
        {
            if (_texCache.TryGetValue(candidate, out var cached))
            {
                if (cached != null) return cached;
                continue;   // known miss
            }
            if (ResourceLoader.Exists(candidate))
            {
                var tex = GD.Load<Texture2D>(candidate);
                _texCache[candidate] = tex;
                if (tex != null) return tex;
            }
            else
            {
                _texCache[candidate] = null;
            }
        }
        return null;
    }

    private static Color ArchetypeColor(NpcArchetypeType a) => a switch
    {
        NpcArchetypeType.Merchant    => UITheme.NegotiationTitleColor,   // gold
        NpcArchetypeType.Commander   => UITheme.TensionHostile,          // red
        NpcArchetypeType.Scholar     => UITheme.Violet,
        NpcArchetypeType.Opportunist => UITheme.TensionCordial,          // green
        NpcArchetypeType.Idealist    => UITheme.NegotiationBodyColor,    // near-white
        NpcArchetypeType.Survivor    => UITheme.TensionStrained,         // amber
        _                            => UITheme.NegotiationNpcColor,
    };
}
