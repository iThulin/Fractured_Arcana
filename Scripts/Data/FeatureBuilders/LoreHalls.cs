using Godot;

// ============================================================
// LoreHalls.cs
//
// Purpose:        The Scriptorum's and the Scrying Chambers' doctrines
//                 (campus_building_upgrades_design_v1 §15). The Hand's
//                 rules live in RemoteCasting (§20).
//
//                 Scriptorum T3:
//                   Refinement    once per timeline, one card may take a
//                                 seventh upgrade point, enough to bring
//                                 BOTH halves to their final stage (4/4).
//                                 Normally the budget (1 shared + 5) forces
//                                 a choice of which half goes all the way.
//                                 The card keeps the point for good.
//                   Dissemination granted signature spells carry the
//                                 guild's best upgrades, +3 budget. The
//                                 rules live in SignatureService (§16).
//                 Scrying Chambers T3:
//                   Third Eye     the ambush portent (was T3 itself), and
//                                 every enemy's intent starts fully revealed
//                                 in expedition fights.
//                   The Hand      the lens casts twice a fight and reaches
//                                 posting and castle defenses (RemoteCasting).
// Layer:          Data / rules
// Collaborators:  Charters (the gate), OwnedCard (Refined, PointsRemaining),
//                 CycleState (RefinementsUsed), CardUpgradeScreen (the Refine
//                 button), CardMintService (minted accounting),
//                 ExpeditionManager (the portent), CombatManager.EnemyIntents
//                 (the reveal).
// ============================================================

/// <summary>Scriptorum and Scrying Chambers doctrine rules. Stateless.</summary>
public static class LoreHalls
{
    public const string ScriptorumId = "scriptorum";
    public const string Refinement = "refinement";
    public const string Dissemination = "dissemination";

    public const string ScryingId = "scrying_chambers";
    public const string ThirdEye = "third_eye";
    public const string TheHand = "the_hand";

    /// <summary>Cards Refinement may perfect per timeline.</summary>
    public const int RefinementsPerCycle = 1;

    public static bool RefinementActive(GuildSaveData save) => Charters.IsActive(save, ScriptorumId, Refinement);

    public static bool ThirdEyeActive(GuildSaveData save) => Charters.IsActive(save, ScryingId, ThirdEye);

    public static int RefinementsLeft(GuildSaveData save)
        => System.Math.Max(0, RefinementsPerCycle - (save?.Cycle?.RefinementsUsed ?? 0));

    /// <summary>Why this card cannot be refined now, or null when it can.</summary>
    public static string CannotRefineReason(GuildSaveData save, OwnedCard card)
    {
        if (!RefinementActive(save))
        {
            return "Needs Refinement chartered at the Grand Hall.";
        }
        if (card == null || card.IsStarter || card.IsRegalia)
        {
            return "Starter cards and Regalia are not refined.";
        }
        if (card.Refined)
        {
            return "Already perfected.";
        }
        if (!card.IsBaseUpgraded)
        {
            return "Give it the shared upgrade first.";
        }
        if (RefinementsLeft(save) <= 0)
        {
            return "The Scriptorum has refined its one card this timeline.";
        }
        return null;
    }

    /// <summary>Refine the card: one more upgrade point, for good. Returns the
    /// report line, or null when refused.</summary>
    public static string TryRefine(GuildSaveData save, OwnedCard card)
    {
        if (CannotRefineReason(save, card) != null)
        {
            return null;
        }
        card.Refined = true;
        save.Cycle.RefinementsUsed++;
        SaveManager.Save();
        GD.Print($"[Refinement] '{card.BlueprintId}' refined ({save.Cycle.RefinementsUsed}/{RefinementsPerCycle}).");
        return "Perfected: this card may take one more upgrade point, enough for both halves to reach their final stage.";
    }
}
