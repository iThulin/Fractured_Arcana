using Godot;

// ============================================================
// DamageNumber.cs
//
// Purpose:        A floating "-N" Label3D that rises and fades above a struck
//                 unit. Colour encodes which pool took the hit: white/red for
//                 health, sky-blue for shield, grey for armour, and a small
//                 "absorbed" tag when nothing reached health. Frees itself.
// Layer:          Systems / Combat / Presentation
// Collaborators:  VfxLibrary (factory), CombatPresenter (caller), UITheme is
//                 deliberately NOT used: these are world-space 3D labels, not
//                 UI chrome, and their colours are a readability contract.
// See:            docs/spell_vfx_pipeline_v1.md §5
// ============================================================

public partial class DamageNumber : Label3D
{
    public float RiseHeight = 0.9f;
    public float Duration = 0.75f;

    private static readonly Color HealthColor = new Color("#FFF4E8");
    private static readonly Color HealthOutline = new Color("#8E1B1B");
    private static readonly Color ShieldColor = new Color("#8FD3FF");
    private static readonly Color ArmorColor = new Color("#C9C4B8");
    private static readonly Color AbsorbedOutline = new Color("#2A2A2A");

    public void Configure(int hpLoss, int shieldLoss, int armorLoss)
    {
        Billboard = BaseMaterial3D.BillboardModeEnum.Enabled;
        NoDepthTest = true;
        Shaded = false;
        DoubleSided = true;
        FontSize = 56;
        OutlineSize = 14;
        PixelSize = 0.006f;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;

        if (hpLoss > 0)
        {
            Text = $"-{hpLoss}";
            Modulate = HealthColor;
            OutlineModulate = HealthOutline;
            if (shieldLoss > 0 || armorLoss > 0)
                Text += $"  ({shieldLoss + armorLoss} absorbed)";
        }
        else if (shieldLoss > 0)
        {
            Text = $"-{shieldLoss} shield";
            Modulate = ShieldColor;
            OutlineModulate = AbsorbedOutline;
            FontSize = 44;
        }
        else if (armorLoss > 0)
        {
            Text = $"-{armorLoss} armour";
            Modulate = ArmorColor;
            OutlineModulate = AbsorbedOutline;
            FontSize = 44;
        }
        else
        {
            Text = "absorbed";
            Modulate = ArmorColor;
            OutlineModulate = AbsorbedOutline;
            FontSize = 36;
        }
    }

    /// <summary>Rise and fade from the current GlobalPosition, then free.</summary>
    public void Play(float speedScale = 1f)
    {
        float dur = Duration / Mathf.Max(0.01f, speedScale);
        var tw = CreateTween().SetParallel(true);
        tw.SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Cubic);
        tw.TweenProperty(this, "global_position",
            GlobalPosition + new Vector3(0f, RiseHeight, 0f), dur);
        tw.TweenProperty(this, "modulate:a", 0f, dur * 0.6f).SetDelay(dur * 0.4f);
        tw.TweenProperty(this, "outline_modulate:a", 0f, dur * 0.6f).SetDelay(dur * 0.4f);
        tw.Chain().TweenCallback(Callable.From(QueueFree));
    }
}
