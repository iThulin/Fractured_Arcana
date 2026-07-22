using Godot;
using System.Collections.Generic;

// ============================================================
// QuestLogView.cs — the ONE quest-list renderer, shared by the
// Campus Quests tab and the global QuestLogScreen overlay so they
// can never drift. Pure UI over QuestLoader/QuestTracker.
// ============================================================

/// <summary>Renders quest cards + the lore codex into caller-supplied VBoxes.</summary>
public static class QuestLogView
{
    /// <summary>Clear <paramref name="box"/> and render grouped quest cards.
    /// Returns the summary line ("N complete · M active · K undiscovered").</summary>
    public static string BuildInto(VBoxContainer box, GuildSaveData save)
    {
        if (box == null) return "";
        foreach (var c in box.GetChildren()) c.QueueFree();
        if (save == null) return "No guild loaded.";

        var quests = QuestLoader.LoadAll();
        int active = 0, done = 0, locked = 0;
        string[] cats = { "Story", "Expansion", "Fragments" };

        foreach (var cat in cats)
        {
            var inCat = new List<QuestDefinition>();
            foreach (var q in quests)
                if (string.Equals(q.Category, cat, System.StringComparison.OrdinalIgnoreCase))
                    inCat.Add(q);
            if (inCat.Count == 0) continue;

            var head = new Label { Text = cat.ToUpper() };
            head.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
            head.AddThemeColorOverride("font_color", UITheme.POINarrative);
            box.AddChild(head);

            foreach (var q in inCat)
            {
                var status = QuestTracker.StatusOf(q, save);
                if (status == QuestStatus.Locked) locked++;
                else if (status == QuestStatus.Complete) done++;
                else active++;
                AddCard(box, q, status, save);
            }
        }
        return $"{done} complete  ·  {active} active  ·  {locked} undiscovered";
    }

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
        parent.AddChild(card);
    }

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

    private static string Prettify(string id)
    {
        if (string.IsNullOrEmpty(id)) return "";
        var parts = id.Replace('_', ' ').Split(' ');
        for (int i = 0; i < parts.Length; i++)
            if (parts[i].Length > 0)
                parts[i] = char.ToUpper(parts[i][0]) + parts[i].Substring(1);
        return string.Join(" ", parts);
    }
}
