using Godot;
using System.Collections.Generic;

// ============================================================
// QuestLogView.cs — the ONE quest-list renderer, shared by the
// Campus Quests tab and the global QuestLogScreen overlay so they
// can never drift. Pure UI over QuestLoader/QuestTracker.
//
// Groups quests by persistence layer (quest spec §7):
//   ETERNAL  ("the Chronicle") — cross-cycle arcs, dossiers,
//            fragments, campus restoration. Never resets.
//   THIS TIMELINE — resolution arcs, companion arcs, kingdom
//            chains, incidentals. Header shows year/lunation.
//   UNFINISHED BUSINESS — archived Timeline quests from past
//            unmakes. Collapsed. The cost of every reset, itemized.
// ============================================================

/// <summary>Renders quest cards + the lore codex into caller-supplied VBoxes.</summary>
public static class QuestLogView
{
    /// <summary>Clear <paramref name="box"/> and render grouped quest cards.
    /// Returns the summary line ("N complete · M active · K undiscovered").</summary>
    public static string BuildInto(VBoxContainer box, GuildSaveData save,
        System.Action<CompanionArcStatus> beginMission = null)
    {
        if (box == null) return "";
        foreach (var c in box.GetChildren()) c.QueueFree();
        if (save == null) return "No guild loaded.";

        var quests = QuestLoader.LoadAll();
        int active = 0, done = 0, locked = 0;

        // ── Partition quests by layer ────────────────────────────────────
        var eternal = new List<QuestDefinition>();
        var timeline = new List<QuestDefinition>();
        foreach (var q in quests)
        {
            if (q.EffectiveLayer == "Eternal")
                eternal.Add(q);
            else
                timeline.Add(q);
        }

        // ── ETERNAL — "the Chronicle" ───────────────────────────────────
        if (eternal.Count > 0)
        {
            AddSectionHeader(box, "ETERNAL");

            // Sub-group Eternal quests by Category for visual structure
            string[] eternalCats = { "Story", "Fragments", "Dossiers", "Expansion" };
            foreach (var cat in eternalCats)
            {
                var inCat = new List<QuestDefinition>();
                foreach (var q in eternal)
                    if (string.Equals(q.Category, cat, System.StringComparison.OrdinalIgnoreCase))
                        inCat.Add(q);
                if (inCat.Count == 0) continue;

                var catLabel = new Label { Text = cat };
                catLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusBuildSmallFontSize);
                catLabel.AddThemeColorOverride("font_color", UITheme.NegotiationHiddenTerm);
                box.AddChild(catLabel);

                int lockedInCat = 0;
                foreach (var q in inCat)
                {
                    var status = QuestTracker.StatusOf(q, save);
                    // Collapsed (user ruling 2026-07-22): undiscovered quests
                    // roll up into one line per category instead of a card
                    // each — eight unmet dossiers were drowning the real log.
                    if (status == QuestStatus.Locked) { locked++; lockedInCat++; continue; }
                    else if (status == QuestStatus.Complete) done++;
                    else active++;
                    AddCard(box, q, status, save);
                }
                if (lockedInCat > 0) AddLockedSummary(box, cat, lockedInCat);
            }
        }

        // ── THIS TIMELINE ───────────────────────────────────────────────
        if (timeline.Count > 0)
        {
            string timelineTitle = "THIS TIMELINE";
            var cycle = save.Cycle;
            if (cycle != null)
            {
                int year = cycle.CampaignYear;
                int lunation = cycle.Calendar?.CurrentLunation ?? 0;
                timelineTitle += $"  —  Year {year}, Lunation {lunation}";
            }
            AddSectionHeader(box, timelineTitle);

            // Sub-group Timeline quests by Category
            string[] timelineCats = { "Story", "Expansion" };
            foreach (var cat in timelineCats)
            {
                var inCat = new List<QuestDefinition>();
                foreach (var q in timeline)
                    if (string.Equals(q.Category, cat, System.StringComparison.OrdinalIgnoreCase))
                        inCat.Add(q);
                if (inCat.Count == 0) continue;

                var catLabel = new Label { Text = cat };
                catLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusBuildSmallFontSize);
                catLabel.AddThemeColorOverride("font_color", UITheme.NegotiationHiddenTerm);
                box.AddChild(catLabel);

                int lockedInCat = 0;
                foreach (var q in inCat)
                {
                    var status = QuestTracker.StatusOf(q, save);
                    // Collapsed (user ruling 2026-07-22): undiscovered quests
                    // roll up into one line per category instead of a card
                    // each — eight unmet dossiers were drowning the real log.
                    if (status == QuestStatus.Locked) { locked++; lockedInCat++; continue; }
                    else if (status == QuestStatus.Complete) done++;
                    else active++;
                    AddCard(box, q, status, save);
                }
                if (lockedInCat > 0) AddLockedSummary(box, cat, lockedInCat);
            }
        }

        // ── COMPANION MISSIONS (Step 9) — every recruited companion's next
        // arc stage, shown regardless of party membership (user directive:
        // the log is the overview; party gating applies at the encounter).
        var missions = CompanionArcTracker.AvailableMissions(save);
        if (missions != null && missions.Count > 0)
        {
            AddSectionHeader(box, "COMPANION MISSIONS");

            foreach (var m in missions)
                AddCompanionMissionCard(box, m, beginMission);
        }

        // ── UNFINISHED BUSINESS (collapsed archive) ─────────────────────
        var unfinished = save.Ledger?.UnfinishedBusiness;
        if (unfinished != null && unfinished.Count > 0)
        {
            AddSectionHeader(box, "UNFINISHED BUSINESS");

            foreach (var rec in unfinished)
            {
                AddUnfinishedCard(box, rec);
            }
        }

        return $"{done} complete  ·  {active} active  ·  {locked} undiscovered";
    }

    // ── Section header ──────────────────────────────────────────────────

    private static void AddSectionHeader(VBoxContainer parent, string text)
    {
        // Spacer before section (except first)
        if (parent.GetChildCount() > 0)
        {
            var spacer = new Control { CustomMinimumSize = new Godot.Vector2(0, 12) };
            parent.AddChild(spacer);
        }

        var head = new Label { Text = text };
        head.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
        head.AddThemeColorOverride("font_color", UITheme.POINarrative);
        parent.AddChild(head);
    }

    // ── Collapsed undiscovered-quest summary ────────────────────────────

    /// <summary>One dim line standing in for all of a category's locked
    /// quests: "❖  6 fragment rumours you have not yet uncovered."</summary>
    private static void AddLockedSummary(VBoxContainer parent, string category, int count)
    {
        string text = string.Equals(category, "Fragments", System.StringComparison.OrdinalIgnoreCase)
            ? (count == 1
                ? "❖  A fragment rumour you have not yet uncovered."
                : $"❖  {count} fragment rumours you have not yet uncovered.")
            : string.Equals(category, "Dossiers", System.StringComparison.OrdinalIgnoreCase)
            ? (count == 1
                ? "❖  An archmage whose forces you have not yet crossed."
                : $"❖  {count} archmagi whose forces you have not yet crossed.")
            : (count == 1
                ? "❖  An undiscovered thread."
                : $"❖  {count} undiscovered threads.");

        var line = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        line.AddThemeFontSizeOverride("font_size", UITheme.CampusBuildSmallFontSize);
        line.AddThemeColorOverride("font_color", UITheme.NegotiationHiddenTerm);
        parent.AddChild(line);
    }

    // ── Quest card (active / complete / locked) ─────────────────────────

    private static void AddCard(VBoxContainer parent, QuestDefinition q, QuestStatus status, GuildSaveData save)
    {
        var card = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        card.AddThemeConstantOverride("separation", 3);

        if (status == QuestStatus.Locked)
        {
            var rumor = new Label
            {
                Text = "❖  " + (q.Category == "Fragments"
                    ? "A fragment rumour you have not yet uncovered."
                    : q.Category == "Dossiers"
                    ? "An archmage whose forces you have not yet crossed."
                    : "An undiscovered thread."),
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            rumor.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
            rumor.AddThemeColorOverride("font_color", UITheme.NegotiationHiddenTerm);
            card.AddChild(rumor);
            parent.AddChild(card);
            return;
        }

        string glyph = status == QuestStatus.Complete ? "✓" : "◆";
        var title = new Label { Text = $"{glyph}  {q.Title}", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        title.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
        title.AddThemeColorOverride("font_color",
            status == QuestStatus.Complete ? UITheme.Gold : UITheme.TextPrimary);
        card.AddChild(title);

        if (!string.IsNullOrEmpty(q.Summary))
        {
            var sum = new Label { Text = q.Summary, AutowrapMode = TextServer.AutowrapMode.WordSmart };
            sum.AddThemeFontSizeOverride("font_size", UITheme.CampusBuildSmallFontSize);
            sum.AddThemeColorOverride("font_color", UITheme.NegotiationHiddenTerm);
            card.AddChild(sum);
        }

        foreach (var o in q.Objectives)
        {
            bool od = QuestTracker.ObjectiveDone(o, save);
            string prog = "";
            if (!string.IsNullOrEmpty(o.Counter))
            {
                var cp = QuestTracker.CounterProgress(o, save);
                prog = $"  ({System.Math.Min(cp.have, cp.need)}/{cp.need})";
            }
            string mark = od ? "✓" : "○";
            var line = new Label { Text = $"   {mark}  {o.Text}{prog}", AutowrapMode = TextServer.AutowrapMode.WordSmart };
            line.AddThemeFontSizeOverride("font_size", UITheme.CampusBuildSmallFontSize);
            line.AddThemeColorOverride("font_color", od ? UITheme.POINegotiation : UITheme.NegotiationHiddenTerm);
            card.AddChild(line);
        }

        // Dossier cards render the revealed weakness-hint TEXT inline — the
        // dossier IS the reward (quest spec §4). Hint text lives on the
        // ArchmageDefinition; the flags only say which are revealed.
        const string dossierPrefix = "q_dossier_";
        if (q.Id.StartsWith(dossierPrefix))
        {
            string archId = q.Id.Substring(dossierPrefix.Length);
            var arch = ArchmageRegistry.Get(archId);
            if (arch != null)
            {
                for (int i = 1; i <= arch.WeaknessHints.Count; i++)
                {
                    bool revealed = save.Ledger?.MetaNarrativeFlags
                        ?.Contains(DossierService.HintFlag(archId, i)) ?? false;
                    var hl = new Label
                    {
                        Text = revealed
                            ? $"      "{arch.WeaknessHints[i - 1]}""
                            : "      —  an unrecorded weakness  —",
                        AutowrapMode = TextServer.AutowrapMode.WordSmart,
                    };
                    hl.AddThemeFontSizeOverride("font_size", UITheme.CampusBuildSmallFontSize);
                    hl.AddThemeColorOverride("font_color",
                        revealed ? UITheme.Gold : UITheme.NegotiationHiddenTerm);
                    card.AddChild(hl);
                }
            }
        }
        parent.AddChild(card);
    }

    // ── Companion mission card (Step 9) ─────────────────────────────────

    /// <summary>Render one companion's next arc stage: name + arc, stage
    /// progress, the stage's title/summary, and where it fires (campus /
    /// expedition, with a party note when the stage needs them along).</summary>
    private static void AddCompanionMissionCard(VBoxContainer parent, CompanionArcStatus m,
        System.Action<CompanionArcStatus> beginMission)
    {
        if (m?.NextStage == null) return;
        var card = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        card.AddThemeConstantOverride("separation", 3);

        var title = new Label
        {
            Text = $"◆  {m.CompanionName} — {m.NextStage.Title}",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        title.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
        title.AddThemeColorOverride("font_color", UITheme.TextPrimary);
        card.AddChild(title);

        string arcLine = $"   {m.ArcName}  ·  stage {m.CurrentStage + 1} of {m.TotalStages}";
        if (m.HasRemembranceBranch)
            arcLine += "  ·  ✦ remembered";
        var arc = new Label { Text = arcLine, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        arc.AddThemeFontSizeOverride("font_size", UITheme.CampusBuildSmallFontSize);
        arc.AddThemeColorOverride("font_color", UITheme.NegotiationHiddenTerm);
        card.AddChild(arc);

        if (!string.IsNullOrEmpty(m.NextStage.Summary))
        {
            var sum = new Label
            {
                Text = $"   {m.NextStage.Summary}",
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            sum.AddThemeFontSizeOverride("font_size", UITheme.CampusBuildSmallFontSize);
            sum.AddThemeColorOverride("font_color", UITheme.NegotiationHiddenTerm);
            card.AddChild(sum);
        }

        // Where it fires + party note. The log shows the mission either way;
        // expedition stages that need the companion say so plainly.
        string where = m.NextStage.Location switch
        {
            "campus" => "at the campus",
            "expedition" => "in the field",
            _ => "anywhere",
        };
        string partyNote = "";
        if (m.NextStage.RequiresParty)
            partyNote = m.IsInParty ? "  ·  they are with you" : "  ·  bring them along";
        var loc = new Label
        {
            Text = $"   ○  {where}{partyNote}",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        loc.AddThemeFontSizeOverride("font_size", UITheme.CampusBuildSmallFontSize);
        loc.AddThemeColorOverride("font_color", UITheme.POINegotiation);
        card.AddChild(loc);

        // Campus-located stages get a launch button when a campus host has
        // supplied a callback (the global overlay stays read-only).
        if (beginMission != null && m.NextStage.Location != "expedition")
        {
            var begin = new Button
            {
                Text = "Begin at the campus",
                CustomMinimumSize = new Godot.Vector2(200, 34),
                SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,
            };
            begin.AddThemeFontSizeOverride("font_size", UITheme.CampusBuildSmallFontSize);
            UITheme.ApplyButtonStyle(begin, isPrimary: true);
            var captured = m;
            begin.Pressed += () => beginMission(captured);
            card.AddChild(begin);
        }

        parent.AddChild(card);
    }

    // ── Unfinished Business card (archived timeline quests) ─────────────

    /// <summary>Render one archived quest from a past unmake — title, summary,
    /// and "abandoned at stage N of M, Timeline VI" epitaph.</summary>
    private static void AddUnfinishedCard(VBoxContainer parent, UnfinishedQuestRecord rec)
    {
        var card = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        card.AddThemeConstantOverride("separation", 2);

        // Title with abandon glyph
        var title = new Label
        {
            Text = $"✕  {rec.Title}",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        title.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
        title.AddThemeColorOverride("font_color", UITheme.NegotiationHiddenTerm);
        card.AddChild(title);

        // Epitaph: "abandoned at stage 2 of 4, Timeline VI (Elementalist)"
        string romanCycle = ToRoman(rec.CycleNumber);
        string epitaph = $"   abandoned at stage {rec.ObjectivesDone} of {rec.ObjectivesTotal}";
        epitaph += $", Timeline {romanCycle}";
        if (!string.IsNullOrEmpty(rec.School))
            epitaph += $" ({rec.School})";

        var epi = new Label
        {
            Text = epitaph,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        epi.AddThemeFontSizeOverride("font_size", UITheme.CampusBuildSmallFontSize);
        epi.AddThemeColorOverride("font_color", UITheme.NegotiationHiddenTerm);
        card.AddChild(epi);

        parent.AddChild(card);
    }

    // ── Lore codex ──────────────────────────────────────────────────────

    /// <summary>Clear <paramref name="box"/> and render the discovered-lore codex.</summary>
    public static void BuildLoreInto(VBoxContainer box, GuildSaveData save)
    {
        if (box == null) return;
        foreach (var c in box.GetChildren()) c.QueueFree();
        var lore = save?.UnlockedLoreEntries;
        if (lore == null || lore.Count == 0)
        {
            var stub = new Label
            {
                Text = "No lore uncovered yet — the world reveals it to those who explore.",
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            stub.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
            stub.AddThemeColorOverride("font_color", UITheme.NegotiationHiddenTerm);
            box.AddChild(stub);
            return;
        }
        foreach (var id in lore)
        {
            var lbl = new Label { Text = "• " + Prettify(id), AutowrapMode = TextServer.AutowrapMode.WordSmart };
            lbl.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
            box.AddChild(lbl);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static string Prettify(string id)
    {
        if (string.IsNullOrEmpty(id)) return "";
        var parts = id.Replace('_', ' ').Split(' ');
        for (int i = 0; i < parts.Length; i++)
            if (parts[i].Length > 0)
                parts[i] = char.ToUpper(parts[i][0]) + parts[i].Substring(1);
        return string.Join(" ", parts);
    }

    private static string ToRoman(int num)
    {
        if (num <= 0) return num.ToString();
        string[] thousands = { "", "M", "MM", "MMM" };
        string[] hundreds = { "", "C", "CC", "CCC", "CD", "D", "DC", "DCC", "DCCC", "CM" };
        string[] tens = { "", "X", "XX", "XXX", "XL", "L", "LX", "LXX", "LXXX", "XC" };
        string[] ones = { "", "I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX" };
        if (num >= 4000) return num.ToString(); // safety
        return thousands[num / 1000] + hundreds[num % 1000 / 100]
             + tens[num % 100 / 10] + ones[num % 10];
    }
}
