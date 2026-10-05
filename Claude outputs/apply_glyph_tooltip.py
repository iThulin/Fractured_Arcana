#!/usr/bin/env python3
# Glyph tile tooltip (2026-10-04). Run from the repo root.
import sys
def edit(rel, pairs):
    s = open(rel, encoding='utf-8').read()
    for old, new in pairs:
        n = s.count(old)
        if n != 1:
            sys.exit(f"{rel}: expected 1 match, found {n}:\n{old[:140]}")
        s = s.replace(old, new)
    open(rel, 'w', encoding='utf-8').write(s)
    print("patched", rel)

# ── GlyphData: display identity + one damage formula ──────────────────
edit('Scripts/Systems/Combat/Glyphs/GlyphData.cs', [
("""    /// <summary>Half of <see cref="SourceCardId"/> that placed this glyph: <c>"top"</c> or <c>"bottom"</c>.</summary>
    public string SourceHalf = "";""",
"""    /// <summary>Half of <see cref="SourceCardId"/> that placed this glyph: <c>"top"</c> or <c>"bottom"</c>.</summary>
    public string SourceHalf = "";

    /// <summary>Display name of the half that placed this glyph (the tier actually cast),
    /// stamped at load time beside <see cref="SourceCardId"/>. Tooltip only.</summary>
    public string SourceName = "";

    /// <summary>Rules text of that half, for the tile tooltip. The sigil is decoration; this
    /// is how the player reads what they cast.</summary>
    public string SourceRulesText = "";"""),
("""        bool friendlyToOwner = who != null && who.TeamId == OwnerTeam;

        if (!friendlyToOwner && who != null)
        {
            int dmg = Damage + bonusDamage + (Owner?.BonusSpellDamage ?? 0);

            if (s?.ActiveEffects != null && OwnerTeam >= 0)
            {
                bool grandDesign = s.ActiveEffects.Any(e =>
                    e is GrandDesignPersistentEffect gd &&
                    gd.OwnerUnit?.TeamId == OwnerTeam &&
                    !e.IsExpired);
                if (grandDesign)
                    dmg *= 2;
            }
            if (dmg > 0)""",
"""        bool friendlyToOwner = who != null && who.TeamId == OwnerTeam;

        if (!friendlyToOwner && who != null)
        {
            int dmg = EffectiveDamage(s, bonusDamage);
            if (dmg > 0)"""),
("""    /// <summary>
    /// Applies this glyph's payload.""",
"""    /// <summary>
    /// The damage this glyph deals right now: base, plus any linked-batch bonus, plus the
    /// owner's spell damage, doubled while its owner's Grand Design is active. One formula
    /// for both <see cref="Fire"/> and the tile tooltip, so the preview cannot drift.
    /// </summary>
    public int EffectiveDamage(GameState s, int bonusDamage = 0)
    {
        if (Damage + bonusDamage <= 0)
            return 0;
        int dmg = Damage + bonusDamage + (Owner?.BonusSpellDamage ?? 0);
        if (s?.ActiveEffects != null && OwnerTeam >= 0)
        {
            bool grandDesign = s.ActiveEffects.Any(e =>
                e is GrandDesignPersistentEffect gd &&
                gd.OwnerUnit?.TeamId == OwnerTeam &&
                !e.IsExpired);
            if (grandDesign)
                dmg *= 2;
        }
        return dmg;
    }

    /// <summary>
    /// Applies this glyph's payload."""),
])

# ── PrepareGlyphEffect: carry the identity onto the glyph ─────────────
edit('Scripts/Cards/Effects/EnchanterEffects.cs', [
("""	public string SourceCardId = "", SourceHalf = "";

	private void Configure(GlyphData g)
	{
		g.SourceCardId = SourceCardId;
		g.SourceHalf = SourceHalf;""",
"""	public string SourceCardId = "", SourceHalf = "";

	/// <summary>Name and rules text of the owning half, stamped beside the id. Tooltip only.</summary>
	public string SourceName = "", SourceRulesText = "";

	private void Configure(GlyphData g)
	{
		g.SourceCardId = SourceCardId;
		g.SourceHalf = SourceHalf;
		g.SourceName = SourceName;
		g.SourceRulesText = SourceRulesText;"""),
])

# ── Loader: stamp name + rules text with the id ───────────────────────
edit('Scripts/Cards/Loader/JsonCardLoader.cs', [
("""        foreach (var e in half.Effects)
            StampGlyphSource(e, half.SourceCardId, half.SourceHalf, 0);
    }

    private static void StampGlyphSource(IEffect e, string cardId, string halfName, int depth)
    {
        if (e == null || depth > 8) return;   // depth guard: card data is authored, not trusted
        if (e is PrepareGlyphEffect p)
        {
            p.SourceCardId = cardId;
            p.SourceHalf = halfName;
        }
        foreach (var child in e.Children)
            StampGlyphSource(child, cardId, halfName, depth + 1);
    }""",
"""        foreach (var e in half.Effects)
            StampGlyphSource(e, half, 0);
    }

    // The half is built from the upgrade-patched JSON, so its name and rules text are the
    // tier actually being cast, which is what the tile tooltip has to show.
    private static void StampGlyphSource(IEffect e, CardHalf half, int depth)
    {
        if (e == null || depth > 8) return;   // depth guard: card data is authored, not trusted
        if (e is PrepareGlyphEffect p)
        {
            p.SourceCardId = half.SourceCardId;
            p.SourceHalf = half.SourceHalf;
            p.SourceName = half.Name ?? "";
            p.SourceRulesText = half.RulesText ?? "";
        }
        foreach (var child in e.Children)
            StampGlyphSource(child, half, depth + 1);
    }"""),
])

# ── Legacy closure glyphs: mirror the payload for display ─────────────
edit('Scripts/Cards/Effects/Effect.cs', [
("""		tile.Glyph = new GlyphData
		{
			OwnerId = caster?.Name ?? "Enchanter",
			OwnerTeam = caster?.TeamId ?? 0,
			GameState = s,
			OnTrigger = (victim, state) =>""",
"""		tile.Glyph = new GlyphData
		{
			OwnerId = caster?.Name ?? "Enchanter",
			OwnerTeam = caster?.TeamId ?? 0,
			GameState = s,
			// Display mirror only: Fire runs OnTrigger and never reads these when it is set.
			Damage = dmg,
			Status = st,
			StatusDuration = dur,
			Reusable = reuse,
			OnTrigger = (victim, state) =>"""),
("""			tile.Glyph = new GlyphData
			{
				OwnerId = casterUnit.Name,
				OwnerTeam = casterUnit.TeamId,
				GameState = s,
				OnTrigger = (victim, state) =>""",
"""			tile.Glyph = new GlyphData
			{
				OwnerId = casterUnit.Name,
				OwnerTeam = casterUnit.TeamId,
				GameState = s,
				// Display mirror only: Fire runs OnTrigger and never reads these when it is set.
				Damage = dmg,
				Status = status,
				StatusDuration = dur,
				OnTrigger = (victim, state) =>"""),
])

# ── Cascade copies keep the parent's name and text ────────────────────
edit('Scripts/Systems/Combat/Glyphs/GlyphManager.cs', [
("""                var owner = g.Owner;
                var trig = g.Trigger;
                Prepare(nbr, owner, ng =>
                {
                    ng.Trigger = trig;""",
"""                var owner = g.Owner;
                var trig = g.Trigger;
                string srcName = g.SourceName, srcText = g.SourceRulesText;
                Prepare(nbr, owner, ng =>
                {
                    ng.SourceName = string.IsNullOrEmpty(srcName) ? "" : srcName + " (spread)";
                    ng.SourceRulesText = srcText;
                    ng.Trigger = trig;"""),
])

# ── TooltipManager: the glyph section ─────────────────────────────────
edit('Scripts/Systems/TooltipManager.cs', [
("""        // Height
        if (tile.Height != 0)""",
"""        // A prepared glyph. The sigil itself is decoration (an easter egg cipher), so this
        // is the player's real read of what is on the tile and how it goes off.
        AddGlyphSection(tile.Glyph);

        // Height
        if (tile.Height != 0)"""),
("""    // ── Content builders ─────────────────────────────────────────""",
"""    // ── Glyph section ────────────────────────────────────────────

    private const int PlayerTeam = 0;
    private const float GlyphTextWidth = 260f;
    private static readonly Color GlyphAccent = new Color(0.62f, 0.82f, 1.00f, 1f);

    private void AddGlyphSection(GlyphData g)
    {
        if (g == null || g.Consumed)
            return;
        bool mine = g.OwnerTeam == PlayerTeam;
        if (g.Invisible && !mine)
            return;                              // a hidden enemy glyph stays hidden

        string owner = g.Owner?.Name ?? g.OwnerId ?? "";
        string name = string.IsNullOrEmpty(g.SourceName) ? "Glyph" : g.SourceName;

        AddSeparator();
        AddColoredRow(name, mine ? "yours" : "enemy", mine ? UITheme.Success : UITheme.Danger);
        if (!string.IsNullOrEmpty(owner) && !mine)
            AddRow("Set by:", owner);

        AddColoredRow("Triggers:", "", GlyphAccent);
        AddWrapped(TriggerText(g, mine, owner), UITheme.TextPrimary);

        foreach (var line in PayloadLines(g, owner))
            AddWrapped(line, UITheme.TextPrimary);

        string life = g.DurationTurns < 0 ? "Until triggered"
                    : g.DurationTurns == 1 ? "1 turn left"
                    : $"{g.DurationTurns} turns left";
        AddRow(g.Reusable ? "Reusable" : "Single use", life);

        if (!string.IsNullOrEmpty(g.SourceRulesText))
            AddWrapped(g.SourceRulesText, UITheme.TextSecondary);
    }

    private static string TriggerText(GlyphData g, bool mine, string owner)
    {
        string who = string.IsNullOrEmpty(owner) ? "its caster" : owner;
        switch (g.Trigger)
        {
            case GlyphTrigger.Enter:
                return mine ? "When an enemy steps onto this tile." : "When one of your units steps onto this tile.";
            case GlyphTrigger.StartOfTurn:
                return mine ? "When an enemy starts its turn on this tile." : "When one of your units starts its turn on this tile.";
            case GlyphTrigger.AllyEnter:
                return mine ? "When one of your units steps onto this tile." : "When an enemy steps onto this tile.";
            case GlyphTrigger.SpellCastNear:
                return g.Radius <= 0 ? "When a spell is cast on this tile."
                     : $"When any spell is cast within {g.Radius} tile{(g.Radius == 1 ? "" : "s")}.";
            case GlyphTrigger.SelfStand:
                return $"Works while {who} stands on it.";
            default:
                return "Only when a linked glyph fires.";
        }
    }

    private static List<string> PayloadLines(GlyphData g, string owner)
    {
        var lines = new List<string>();

        int dmg = g.EffectiveDamage(g.GameState);
        if (dmg > 0 && !string.IsNullOrEmpty(g.Status))
            lines.Add($"Deals {dmg} damage and applies {g.Status} for {g.StatusDuration} turn{(g.StatusDuration == 1 ? "" : "s")}.");
        else if (dmg > 0)
            lines.Add($"Deals {dmg} damage.");
        else if (!string.IsNullOrEmpty(g.Status))
            lines.Add($"Applies {g.Status} for {g.StatusDuration} turn{(g.StatusDuration == 1 ? "" : "s")}.");

        var ally = new List<string>();
        if (g.AllyArmor > 0) ally.Add($"+{g.AllyArmor} armor");
        if (g.AllyShield > 0) ally.Add($"+{g.AllyShield} shield");
        if (g.AllyDamage > 0) ally.Add($"+{g.AllyDamage} spell damage");
        if (g.AllyMana > 0) ally.Add($"+{g.AllyMana} mana");
        if (ally.Count > 0)
            lines.Add((g.Trigger == GlyphTrigger.SelfStand ? "Grants " : "Allies gain ") + string.Join(", ", ally) + ".");

        var pay = new List<string>();
        if (g.OwnerDraw > 0) pay.Add($"draw {g.OwnerDraw}");
        if (g.OwnerMana > 0) pay.Add($"+{g.OwnerMana} mana");
        if (g.OwnerWeave > 0) pay.Add($"+{g.OwnerWeave} Weave");
        if (g.OwnerHeal > 0) pay.Add($"heal {g.OwnerHeal}");
        if (pay.Count > 0)
            lines.Add($"{(string.IsNullOrEmpty(owner) ? "Caster" : owner)}: " + string.Join(", ", pay) + ".");

        if (g.CascadeSpread > 0)
            lines.Add($"Spreads a copy to {g.CascadeSpread} adjacent tile{(g.CascadeSpread == 1 ? "" : "s")} when it fires.");
        if (g.LinkId != 0)
            lines.Add(g.CumulativeBonus > 0
                ? $"Linked: fires with its network, +{g.CumulativeBonus} damage per glyph that fires."
                : "Linked: fires with its network.");

        if (lines.Count == 0 && g.OnTrigger != null)
            lines.Add("Effect: see the spell that placed it.");
        return lines;
    }

    private void AddWrapped(string text, Color color)
    {
        var lbl = new Label
        {
            Text = text,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(GlyphTextWidth, 0),
        };
        lbl.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        lbl.AddThemeColorOverride("font_color", color);
        lbl.MouseFilter = MouseFilterEnum.Ignore;
        _content.AddChild(lbl);
    }

    // ── Content builders ─────────────────────────────────────────"""),
])
