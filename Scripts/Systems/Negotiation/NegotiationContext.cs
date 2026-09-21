using System.Collections.Generic;

// ============================================================
// NegotiationContext.cs
//
// Purpose:        Static context carrier for negotiation scene
//                 swaps. Mirrors the pattern of PlayerSession /
//                 EquipmentLoadout: set before scene change,
//                 read on entry, results written back, run
//                 manager reads results after return.
//                 v3: carries the reward verbs (fuel, chart,
//                 reveal, anchor, safe conduct, lore) alongside
//                 gold / rep / supplies / spell.
// Layer:          Data
// Collaborators:  OverworldRunManager.cs / EncounterRouter.cs
//                 (input writers + result readers),
//                 NegotiationManager.cs (consumes input + writes
//                 results), ExpeditionManager.OnNegotiationReturned
//                 (applies results)
// See:            docs/negotiation_ledger_spec_v1.md §2f, §11
// ============================================================

/// <summary>Static scratchpad threaded through the scene swap between overworld and negotiation. Input fields set by the run manager before swap; output fields populated by the negotiation scene on completion.</summary>
public static class NegotiationContext
{
    // ── Input (set before scene swap) ───────────────────────────────────
    public static string EncounterId = "";
    public static string HexCoordKey = "";          // "q,r" for the triggering hex

    /// <summary>Archetype of the NPC (C4 echo routing for deal deeds).</summary>
    public static string NpcArchetype = "";

    /// <summary>Kingdom whose territory the negotiation was triggered in,
    /// or "" for non-kingdom tiles. Drives the court-standing goodwill
    /// lookup and the deal-deed echo route on return.</summary>
    public static string OriginKingdomId = "";

    /// <summary>S3 (Beguile): goodwill added at table-open ("one band more
    /// favorable"). Set by the expedition layer when an armed Beguile is
    /// consumed; consumed (zeroed) by NegotiationManager on open. v2 called
    /// this TensionShift and subtracted it; v3 adds it to goodwill.</summary>
    public static int TensionShift = 0;

    /// <summary>S5 (Parley Compulsion): true when this table came from a
    /// compelled patrol. On return, a Warm close buries the PatrolCompelled
    /// echo in flight (ExpeditionManager).</summary>
    public static bool FromCompulsion = false;

    // ── Output (set by NegotiationScene on completion) ──────────────────
    public static bool HasResult = false;
    public static bool DealAccepted = false;
    public static int GoldDelta = 0;
    public static int ReputationDelta = 0;

    /// <summary>Supplies moved by the deal. Positive rides home at risk;
    /// negative deducts from the treasury on return.</summary>
    public static int SuppliesDelta = 0;

    /// <summary>True when the signed deal included supply-lines intel.</summary>
    public static bool RevealSupplyCaches = false;

    /// <summary>Expedition fuel moved by the deal (Clause.FuelDelta). Applied
    /// to ExpeditionManager.StepsRemaining on return, floored at 0.</summary>
    public static int FuelDelta = 0;

    /// <summary>Chart: radius of the hex disc charted around the
    /// negotiation's hex on return (0 = none).</summary>
    public static int ChartRadius = 0;

    /// <summary>Reveal: PoiKind names; one undiscovered POI of each kind in
    /// the origin kingdom is discovered on return.</summary>
    public static List<string> RevealPoiKinds = new();

    /// <summary>Anchor: the negotiation's hex becomes a supply anchor for the
    /// rest of the expedition.</summary>
    public static bool AnchorHere = false;

    /// <summary>Safe conduct: patrols stand down for this many steps.</summary>
    public static int SafeConductSteps = 0;

    /// <summary>Lore entries unlocked by the deal.</summary>
    public static List<string> LoreUnlocks = new();

    public static string FactionId = "";

    /// <summary>S4: spell id taught by the deal (Warm-sealed clause), or "".</summary>
    public static string SpellGranted = "";

    /// <summary>S5: true when the table SIGNED Warm (the v3 "Cordial"): the
    /// compulsion-echo burial gate, with DealAccepted.</summary>
    public static bool ResolvedCordial = false;

    /// <summary>True when the table collapsed (goodwill hit zero) AND the
    /// counterpart escalates. ExpeditionManager launches the fight.</summary>
    public static bool Escalated = false;

    /// <summary>§6a: the deal's court consequence was settled at the table.</summary>
    public static bool RegardSettledAtTable = false;

    public static void SetResult(bool accepted, int gold, int rep, string factionId,
                                 string spellGranted = "", bool resolvedCordial = false,
                                 int supplies = 0, bool revealSupplyCaches = false,
                                 int fuel = 0, bool escalated = false,
                                 int chartRadius = 0, List<string> revealPoiKinds = null,
                                 bool anchorHere = false, int safeConductSteps = 0,
                                 List<string> loreUnlocks = null)
    {
        HasResult = true;
        Escalated = escalated;
        DealAccepted = accepted;
        GoldDelta = gold;
        ReputationDelta = rep;
        SuppliesDelta = supplies;
        FuelDelta = fuel;
        RevealSupplyCaches = revealSupplyCaches;
        FactionId = factionId;
        SpellGranted = spellGranted;
        ResolvedCordial = resolvedCordial;
        ChartRadius = chartRadius;
        RevealPoiKinds = revealPoiKinds ?? new List<string>();
        AnchorHere = anchorHere;
        SafeConductSteps = safeConductSteps;
        LoreUnlocks = loreUnlocks ?? new List<string>();
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
        FuelDelta = 0;
        RevealSupplyCaches = false;
        ChartRadius = 0;
        RevealPoiKinds = new List<string>();
        AnchorHere = false;
        SafeConductSteps = 0;
        LoreUnlocks = new List<string>();
        FactionId = "";
        SpellGranted = "";
        ResolvedCordial = false;
        Escalated = false;
        RegardSettledAtTable = false;
        EncounterId = "";
        HexCoordKey = "";
        NpcArchetype = "";
        OriginKingdomId = "";
    }
}
