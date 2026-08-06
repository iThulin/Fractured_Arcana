// ============================================================
// NegotiationContext.cs
//
// Purpose:        Static context carrier for negotiation scene
//                 swaps. Mirrors the pattern of PlayerSession /
//                 EquipmentLoadout — set before scene change,
//                 read on entry, results written back, run
//                 manager reads results after return.
// Layer:          Data
// Collaborators:  OverworldRunManager.cs / EncounterRouter.cs
//                 (input writers + result readers),
//                 NegotiationManager.cs (consumes input + writes
//                 results)
// See:            README §6 — Negotiation
// ============================================================

/// <summary>Static scratchpad threaded through the scene swap between overworld and negotiation. Input fields set by the run manager before swap; output fields populated by the negotiation scene on completion.</summary>
public static class NegotiationContext
{
    // ── Input (set before scene swap) ───────────────────────────────────
    public static string EncounterId = "";
    public static string HexCoordKey = "";          // "q,r" for the triggering hex

    /// <summary>Archetype of the NPC (C4 echo routing for deal deeds).
    /// Set alongside EncounterId before the scene swap.</summary>
    public static string NpcArchetype = "";

    /// <summary>Kingdom whose territory the negotiation was triggered in,
    /// or "" for non-kingdom tiles (wilds, convergence). Set at trigger
    /// time by ExpeditionManager. Drives BOTH the starting-tension lookup
    /// (court standing for kingdom NPCs) and the deal-deed echo route on
    /// return. Distinct from the authored FactionId, which stays the
    /// non-kingdom faction key.</summary>
    public static string OriginKingdomId = "";

    /// <summary>S3 (Beguile, overworld_spell_system §7f): points subtracted
    /// from the encounter's starting tension — "one band more favorable",
    /// implemented as −2 tension. Set by the expedition layer when an armed
    /// Beguile is consumed; consumed (zeroed) by NegotiationManager on open.</summary>
    public static int TensionShift = 0;

    /// <summary>S5 (Parley Compulsion §7f): true when this table came from
    /// a compelled patrol. On return, a Cordial close buries the
    /// PatrolCompelled echo in flight (ExpeditionManager). Set only by
    /// TriggerPatrolNegotiation.</summary>
    public static bool FromCompulsion = false;


    // ── Output (set by NegotiationScene on completion) ──────────────────
    public static bool HasResult = false;
    public static bool DealAccepted = false;
    public static int GoldDelta = 0;
    public static int ReputationDelta = 0;

    /// <summary>Supplies moved by the deal (docs/supply_cache_spec_v1) —
    /// positive rides home with the expedition as at-risk SuppliesEarned,
    /// negative deducts from the treasury on return (ExpeditionManager.
    /// OnNegotiationReturned).</summary>
    public static int SuppliesDelta = 0;

    /// <summary>True when the signed deal included supply-lines intel — on
    /// return, every cache in OriginKingdomId is revealed
    /// (SupplyCacheSystem.RevealCachesInKingdom).</summary>
    public static bool RevealSupplyCaches = false;

    /// <summary>Expedition range moved by the deal (DealTerm.StepsDelta).
    /// Applied to ExpeditionManager.StepsRemaining on return, floored at 0 —
    /// the same shape as NarrativeChoice.StepDelta. Before 2026-08-06 this
    /// channel was authored in JSON, weighted by the NPC AI, and then
    /// silently dropped: frontier_wilds_commander's "safe_passage" promised
    /// +3 steps and delivered nothing.</summary>
    public static int StepsDelta = 0;

    public static string FactionId = "";

    /// <summary>S4 (overworld_spell_system §11): spell id taught by a deal
    /// that closed in the Cordial zone, or "". ExpeditionManager learns it
    /// on return (KnownSpellIds — persists on the cycle save).</summary>
    public static string SpellGranted = "";

    /// <summary>S5: true when the table ENDED in the Cordial zone — the
    /// compulsion-echo burial gate (with DealAccepted), set alongside the
    /// other results by NegotiationManager.</summary>
    public static bool ResolvedCordial = false;

    public static void SetResult(bool accepted, int gold, int rep, string factionId,
                                 string spellGranted = "", bool resolvedCordial = false,
                                 int supplies = 0, bool revealSupplyCaches = false,
                                 int steps = 0)
    {
        HasResult = true;
        DealAccepted = accepted;
        GoldDelta = gold;
        ReputationDelta = rep;
        SuppliesDelta = supplies;
        StepsDelta = steps;
        RevealSupplyCaches = revealSupplyCaches;
        FactionId = factionId;
        SpellGranted = spellGranted;
        ResolvedCordial = resolvedCordial;
    }

    public static void Clear()
    {
        TensionShift = 0;
        FromCompulsion = false;
        HasResult = false;
        DealAccepted = false;
        GoldDelta = 0;
        ReputationDelta = 0;
        SuppliesDelta = 0;
        StepsDelta = 0;
        RevealSupplyCaches = false;
        FactionId = "";
        SpellGranted = "";
        ResolvedCordial = false;
        EncounterId = "";
        HexCoordKey = "";
        NpcArchetype = "";
        OriginKingdomId = "";
    }
}