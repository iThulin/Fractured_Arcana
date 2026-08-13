using Godot;
using System.Linq;

// ============================================================
// CompanionDossier.cs
//
// Purpose:        The shared people-reading surface (companion_item
//                 _systems v2.1 §8): one card that renders a
//                 Companion's dossier — name, class, school, trait,
//                 loyalty tier, arc stage, stances, combat stats —
//                 identically wherever people are read. K3 consumer:
//                 the hiring hall. Future consumers: the Muster
//                 screen and the court dispatch screen (same card,
//                 different action button).
// Layer:          UI
// Collaborators:  CompanionDefinition.cs (the model), UITheme.cs,
//                 StanceRegistry (stance display names),
//                 CityServicesHost.cs (K3 host).
// Notes:          Pure builder — no state, no signals of its own.
//                 The host supplies the action button text/handler.
// ============================================================

/// <summary>Builds a dossier card Control for one companion. The card is
/// read-only; the optional action button (hire / assign / dispatch) belongs
/// to the host, which knows the price and the verb.</summary>
public static class CompanionDossier
{
    /// <summary>Build one dossier card. <paramref name="actionText"/> null/empty
    /// = no button (pure display). <paramref name="actionEnabled"/> lets hosts
    /// grey the verb (can't afford / slot full) while keeping the dossier
    /// readable — the player should always be able to READ people.</summary>
    public static Control Build(Companion c, string actionText = null,
        bool actionEnabled = true, System.Action onAction = null)
    {
        var card = new PanelContainer();
        card.AddThemeStyleboxOverride("panel", UITheme.MakePanelStyle(
            UITheme.CompanionCardBg, UITheme.CompanionCardBorderInactive));

        var pad = new MarginContainer();
        pad.AddThemeConstantOverride("margin_left", 12);
        pad.AddThemeConstantOverride("margin_right", 12);
        pad.AddThemeConstantOverride("margin_top", 10);
        pad.AddThemeConstantOverride("margin_bottom", 10);
        card.AddChild(pad);

        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 4);
        pad.AddChild(col);

        // ── Header: name + the identity line ────────────────────────────
        var name = new Label { Text = c.Name };
        name.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
        name.AddThemeColorOverride("font_color", UITheme.Gold);
        col.AddChild(name);

        string schoolBit = (c.School != "None" && !string.IsNullOrEmpty(c.School))
            ? $" · {c.School}" : "";
        var identity = new Label
        {
            Text = $"{c.UnitClass}{schoolBit} · {c.PersonalityTrait} · " +
                   $"{c.GetLoyaltyTier()}{ArcBit(c)}",
        };
        identity.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        identity.AddThemeColorOverride("font_color", UITheme.TextDim);
        col.AddChild(identity);

        // ── Stats line (martials show the martial block; arcane show mana) ─
        var stats = new Label { Text = StatLine(c) };
        stats.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        col.AddChild(stats);

        // ── Stances (pre-training is the legible quality signal) ────────
        if (c.TrainedStanceIds.Count > 0)
        {
            var names = c.TrainedStanceIds
                .Select(id => StanceRegistry.Get(id)?.DisplayName ?? id);
            var stances = new Label { Text = $"Trained: {string.Join(", ", names)}" };
            stances.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
            stances.AddThemeColorOverride("font_color", UITheme.TextDim);
            col.AddChild(stances);
        }

        // ── Backstory (one line, wrapped) ────────────────────────────────
        if (!string.IsNullOrEmpty(c.Backstory))
        {
            var story = new Label { Text = c.Backstory };
            story.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
            story.AddThemeColorOverride("font_color", UITheme.TextDim);
            story.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            col.AddChild(story);
        }

        // ── Host action ──────────────────────────────────────────────────
        if (!string.IsNullOrEmpty(actionText))
        {
            var btn = new Button
            {
                Text = actionText,
                Disabled = !actionEnabled,
                SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,
            };
            btn.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
            UITheme.ApplyButtonStyle(btn, isPrimary: actionEnabled);
            if (onAction != null) btn.Pressed += () => onAction();
            col.AddChild(btn);
        }

        return card;
    }

    private static string ArcBit(Companion c) =>
        c.ArcStage > 0 ? $" · Arc {c.ArcStage}/4" : "";

    private static string StatLine(Companion c) => c.UnitClass switch
    {
        "Arcane" => $"HP {c.BaseHP} · Mana {c.BaseMana} · Speed {c.BaseSpeed}",
        _ => $"HP {c.BaseHP} · Dmg {c.BaseAttackDamage} · Rng {c.BaseAttackRange}" +
             $" · Spd {c.BaseSpeed} · Armor {c.BaseArmor}",
    };
}
