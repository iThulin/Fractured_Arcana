using System.Collections.Generic;

// ============================================================
// QuestNotifier.cs: turns quest-state CHANGES into toast messages
// by diffing a cheap before/after snapshot around a mutation.
// Detects unlocks, objective completions, quest completions, AND
// per-increment counter progress. No persisted notification state.
// ============================================================

/// <summary>Toast category (drives colour/prefix in ToastManager).</summary>
public enum QuestToastKind { Unlock, Progress, Complete }

/// <summary>A single quest notification.</summary>
public struct QuestToast
{
    public string Text;
    public QuestToastKind Kind;
    public QuestToast(string text, QuestToastKind kind) { Text = text; Kind = kind; }
}

/// <summary>Boolean events + counter values captured at a moment in time.</summary>
public class QuestSnapshot
{
    public HashSet<string> Events = new();      // unlock:<id> / obj:<id>:<i> / done:<id>
    public Dictionary<string, int> Counters = new(); // "<id>:<i>" -> current count
}

/// <summary>Diffs quest state to announce unlocks, objective ticks, counter
/// progress, and completions.</summary>
public static class QuestNotifier
{
    public static QuestSnapshot Snapshot(GuildSaveData save)
    {
        var snap = new QuestSnapshot();
        if (save == null) return snap;
        foreach (var q in QuestLoader.LoadAll())
        {
            var status = QuestTracker.StatusOf(q, save);
            if (status == QuestStatus.Locked) continue;
            snap.Events.Add("unlock:" + q.Id);
            if (q.Objectives != null)
                for (int i = 0; i < q.Objectives.Count; i++)
                {
                    var o = q.Objectives[i];
                    if (QuestTracker.ObjectiveDone(o, save))
                        snap.Events.Add($"obj:{q.Id}:{i}");
                    if (!string.IsNullOrEmpty(o.Counter))
                        snap.Counters[$"{q.Id}:{i}"] = QuestTracker.CounterProgress(o, save).have;
                }
            if (status == QuestStatus.Complete)
                snap.Events.Add("done:" + q.Id);
        }
        return snap;
    }

    /// <summary>Stamp permanent completions, then return toasts for everything that
    /// changed since <paramref name="before"/>: a just-unlocked quest (one line),
    /// an objective ticking, counter progress (have/need), or a completion.</summary>
    public static List<QuestToast> NotifyNew(QuestSnapshot before, GuildSaveData save)
    {
        var msgs = new List<QuestToast>();
        if (save == null || before == null) return msgs;
        QuestTracker.SyncCompletions(save);
        var now = Snapshot(save);

        foreach (var q in QuestLoader.LoadAll())
        {
            string uk = "unlock:" + q.Id, dk = "done:" + q.Id;
            if (!now.Events.Contains(uk)) continue;                 // still locked

            if (!before.Events.Contains(uk))
            {
                msgs.Add(new QuestToast("New quest: " + q.Title, QuestToastKind.Unlock));
                continue;                                           // initial objectives implied
            }
            if (now.Events.Contains(dk) && !before.Events.Contains(dk))
            {
                msgs.Add(new QuestToast("Quest complete: " + q.Title, QuestToastKind.Complete));
                continue;                                           // completion covers final objective
            }
            if (q.Objectives == null) continue;

            for (int i = 0; i < q.Objectives.Count; i++)
            {
                var o = q.Objectives[i];
                string ok = $"obj:{q.Id}:{i}";
                bool wasObj = before.Events.Contains(ok);
                bool isObj = now.Events.Contains(ok);

                if (isObj && !wasObj)
                {
                    msgs.Add(new QuestToast($"{q.Title}: {o.Text}", QuestToastKind.Progress));
                    continue;
                }
                if (!isObj && !string.IsNullOrEmpty(o.Counter))
                {
                    string ck = $"{q.Id}:{i}";
                    int hb = before.Counters.TryGetValue(ck, out var b) ? b : 0;
                    int hn = now.Counters.TryGetValue(ck, out var n) ? n : 0;
                    if (hn > hb)
                        msgs.Add(new QuestToast(
                            $"{q.Title}: {o.Text} ({hn}/{o.CounterTarget})", QuestToastKind.Progress));
                }
            }
        }
        return msgs;
    }
}
