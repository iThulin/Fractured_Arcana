using Godot;

// ============================================================
// TimelineAnchors.cs
//
// Purpose:        How a timeline is lost before the Convergence
//                 (docs/timeline_loss_anchors_v1.md; ruled 2026-11-10).
//                 A fight lost with the player's wizard on the board
//                 breaks an Anchor. When the last one breaks the
//                 timeline is unmade: it archives as "WizardFallen"
//                 and the campus begins the next one. (Ruled 2026-11-10,
//                 01:01: the count is the falls a timeline has in it,
//                 so 2 Anchors means the second fall ends it.)
//
//                 A timeline opens with its Anchors full. Nothing
//                 refills them inside a timeline, and holding the
//                 timeline into a new year (SaveManager.ContinueCampaign)
//                 keeps them as they stand. The maximum comes from the
//                 permanent layer: 2, +1 at Grand Hall II, +1 at Grand
//                 Hall III, +1 for three shards held, to 5.
//
//                 Never costs an Anchor: a fight the wizard is not in,
//                 a scripted fight (EncounterRouter.SuppressRewards), an
//                 exam, the Convergence, a debug fight.
// Layer:          Data / rules
// Collaborators:  CycleState (AnchorsSpent, TimelineLost), EncounterRouter
//                 (the lost fight), CombatManager.SpawnTestUnits (who was
//                 on the board), TimelineEndScreen (the outcome, the next reading),
//                 StrategicView (a lost timeline reloaded), HudManager
//                 (the count), RegaliaService (shards held).
// ============================================================

/// <summary>The Anchor rules. Stateless: the count spent lives on the cycle,
/// the maximum is derived from the ledger.</summary>
public static class TimelineAnchors
{
    // Starting values (TA2, TA3). Tune here.
    public const int BaseAnchors = 2;
    public const int MaxAnchors = 5;
    public const int ShardsForAnchor = 3;

    public const string GrandHallId = "grand_hall";

    /// <summary>LoopRecord.Outcome for a timeline lost this way.</summary>
    public const string OutcomeWizardFallen = "WizardFallen";

    /// <summary>The Grand Hall's built tier. Blight is ignored on purpose: an
    /// overrun hall must not take an Anchor away in the middle of a timeline.</summary>
    private static int GrandHallTier(GuildSaveData save)
    {
        var buildings = save?.Ledger?.Buildings;
        if (buildings == null)
        {
            return 0;
        }
        foreach (var b in buildings)
        {
            if (b != null && b.Id == GrandHallId)
            {
                return b.Tier;
            }
        }
        return 0;
    }

    /// <summary>Anchors a timeline opens with.</summary>
    public static int Max(GuildSaveData save)
    {
        int n = BaseAnchors;
        int hall = GrandHallTier(save);
        if (hall >= 2)
        {
            n++;
        }
        if (hall >= 3)
        {
            n++;
        }
        if (RegaliaService.ShardsCollected(save) >= ShardsForAnchor)
        {
            n++;
        }
        return Mathf.Min(MaxAnchors, n);
    }

    /// <summary>Anchors still holding in this timeline.</summary>
    public static int Left(GuildSaveData save)
    {
        var cycle = save?.Cycle;
        return cycle == null ? 0 : Mathf.Max(0, Max(save) - cycle.AnchorsSpent);
    }

    /// <summary>A fight was lost with the wizard on the board. Breaks an Anchor;
    /// when that was the last one, marks the timeline lost. Returns true when the
    /// timeline is unmade. <paramref name="line"/> is what the player is told.</summary>
    public static bool SpendOnDefeat(GuildSaveData save, out string line)
    {
        line = "You lost the fight. No rewards.";
        var cycle = save?.Cycle;
        if (cycle == null)
        {
            return false;
        }
        if (cycle.TimelineLost)
        {
            line = "This timeline is already over.";
            return true;
        }
        cycle.AnchorsSpent++;
        SaveManager.MarkDirty();
        int left = Left(save);
        if (left <= 0)
        {
            cycle.TimelineLost = true;
            line = "You lost your last Anchor. This timeline is over.";
            GD.Print("[Anchors] The last Anchor broke. The timeline is lost.");
            return true;
        }
        line = left == 1
            ? "You lost an Anchor. 1 left. Lose it and this timeline ends."
            : $"You lost an Anchor. {left} left. Lose them all and this timeline ends.";
        GD.Print($"[Anchors] An Anchor broke ({left} of {Max(save)} left).");
        return false;
    }
}
