using Godot;

// ============================================================
// ShadowMarket.cs
//
// Purpose:        The Veiled Concord's two-sided marketplace (phase
//                 E3) — the player-facing verbs, resolved as domain
//                 calls (debug now, UI later). SELL side: fence a
//                 known courtier secret for Concord Favor, with a
//                 trace risk that scales with Marked (ruling #3 —
//                 only a TRACED sale bites the court). BUY side:
//                 commission Tier A/B contracts (Plant Asset,
//                 Purchase Intel, Theft) that ripen and resolve on
//                 the lunation tick (ShadowTick.ResolveContracts).
//
//                 Currencies: Concord Favor (spent here, earned by
//                 Cutouts and sales) and Marked (the shadow-world's
//                 memory of the guild, raised by every dealing).
//                 Gates: EffectiveBand — contact floors the guild at
//                 Known; Theft needs Trusted (lifetime dealings).
//
//                 SCOPE (E3): Plant Asset / Purchase Intel / Theft.
//                 Sabotage is E4 (its warfront/corruption hooks),
//                 Extraction is E5, Assassination is E6 — none are
//                 registered here ahead of their effects (no stubs).
//                 Prisoner/contraband sales await a capture system.
// Layer:          System
// Collaborators:  ShadowState.cs (costs/gates/Marked), ShadowTick.cs
//                 (contract resolution + Marked decay), CouncilState
//                 (Favor/Marked/dealings/contracts), CourtState
//                 (secrets sold / stung)
// See:            espionage_veiled_concord_spec_v1.md §3b, §3c, §3d
// ============================================================

/// <summary>Result of a marketplace action — success flag plus a player-facing
/// line for the debug console or a later toast.</summary>
public struct ShadowMarketResult
{
    public bool Ok;
    public string Message;

    public static ShadowMarketResult Fail(string msg) => new() { Ok = false, Message = msg };
    public static ShadowMarketResult Pass(string msg) => new() { Ok = true, Message = msg };
}

public static class ShadowMarket
{
    /// <summary>Clamp Marked into range after a delta. The single mutation point
    /// for the meter, so thresholds and decay always read a sane value.</summary>
    public static void AddMarked(CouncilState council, int delta)
    {
        council.Marked = Mathf.Clamp(council.Marked + delta, 0, ShadowVocab.MarkedMax);
    }

    // ── Sell side (§3b) ──────────────────────────────────────────────────

    /// <summary>Fence a known courtier secret to the Concord for Favor. The
    /// guild trades away its copy (the secret is no longer held as leverage). A
    /// trace roll — chance rising with Marked — may pin the leak on the guild,
    /// souring the sold-out courtier and deepening Marked.</summary>
    public static ShadowMarketResult SellSecret(CycleState cycle, string kingdomId,
                                                string courtierId)
    {
        var council = cycle?.Council;
        if (council == null || !council.ConcordContacted)
        {
            return ShadowMarketResult.Fail("The Concord is not yet known to the guild.");
        }
        if (!council.Courts.TryGetValue(kingdomId, out var court))
        {
            return ShadowMarketResult.Fail("No such court.");
        }
        var courtier = court.GetCourtier(courtierId);
        if (courtier == null || !courtier.SecretKnown)
        {
            return ShadowMarketResult.Fail("No held secret to sell there.");
        }

        // Trade the secret away for coin. The copy is spent — leverage or gold,
        // not both.
        courtier.SecretKnown = false;
        council.ConcordFavor += ShadowVocab.FavorSellSecret;
        council.ConcordDealings += 1;

        int traceChance = ShadowVocab.SellTraceBasePercent
                          + ShadowVocab.SellTracePerMarked * council.Marked;
        bool traced = (int)(GD.Randi() % 100) < traceChance;
        string tail;
        if (traced)
        {
            courtier.Regard = Mathf.Clamp(courtier.Regard - ShadowVocab.TracedSellRegardHit, -3, 3);
            AddMarked(council, ShadowVocab.MarkedGainTracedSell);
            tail = $" The sale was traced — {courtier.DisplayName} knows, and the shadows note you " +
                   $"(Marked {council.Marked}).";
        }
        else
        {
            tail = " The sale stayed quiet.";
        }

        SaveManager.MarkDirty();
        return ShadowMarketResult.Pass(
            $"Fenced a secret for {ShadowVocab.FavorSellSecret} favor " +
            $"({council.ConcordFavor} banked).{tail}");
    }

    // ── Buy side (§3c) ───────────────────────────────────────────────────

    public static ShadowMarketResult CommissionPlantAsset(CycleState cycle, string kingdomId,
                                                          string courtierId = "")
    {
        return Commission(cycle, ShadowVocab.ContractPlantAsset, kingdomId, courtierId,
            ShadowVocab.FavorCostPlantAsset, ShadowVocab.ContractDurPlantAsset,
            ConcordStandingBand.Known);
    }

    public static ShadowMarketResult CommissionPurchaseIntel(CycleState cycle, string kingdomId)
    {
        return Commission(cycle, ShadowVocab.ContractPurchaseIntel, kingdomId, "",
            ShadowVocab.FavorCostPurchaseIntel, ShadowVocab.ContractDurPurchaseIntel,
            ConcordStandingBand.Known);
    }

    public static ShadowMarketResult CommissionTheft(CycleState cycle, string kingdomId,
                                                    string courtierId)
    {
        return Commission(cycle, ShadowVocab.ContractTheft, kingdomId, courtierId,
            ShadowVocab.FavorCostTheft, ShadowVocab.ContractDurTheft,
            ConcordStandingBand.Trusted);
    }

    /// <summary>Commission a Sabotage (§3c) — break an active siege pressing the
    /// target kingdom, or stall its next corruption tick (subject to the §4 cap
    /// at resolution). The variant rides in TargetId.</summary>
    public static ShadowMarketResult CommissionSabotage(CycleState cycle, string kingdomId,
                                                       string variant)
    {
        if (variant != ShadowVocab.SabotageSiege && variant != ShadowVocab.SabotageCorruption)
        {
            return ShadowMarketResult.Fail("Unknown sabotage variant.");
        }
        return Commission(cycle, ShadowVocab.ContractSabotage, kingdomId, variant,
            ShadowVocab.FavorCostSabotage, ShadowVocab.ContractDurSabotage,
            ConcordStandingBand.Known);
    }

    /// <summary>Commission an Extraction (§3c) — free an imprisoned envoy without
    /// mounting the Prison-POI expedition. Trusted-gated. Optionally target a
    /// specific captive by companion id; otherwise the first held is freed.</summary>
    public static ShadowMarketResult CommissionExtraction(CycleState cycle, string kingdomId,
                                                         string companionId = "")
    {
        return Commission(cycle, ShadowVocab.ContractExtraction, kingdomId, companionId,
            ShadowVocab.FavorCostExtraction, ShadowVocab.ContractDurExtraction,
            ConcordStandingBand.Trusted);
    }

    /// <summary>Commission an Assassination (§3c, Tier C) — remove a courtier
    /// permanently. Gated on the Inner band AND Undercroft III (the spine's
    /// deepest tier unlocks the cabal's lethal work). Irreversible.
    ///
    /// SEAM: per spec §8 this is meant to play as an interactive Concord-broker
    /// negotiation (Opportunist archetype, Concord-state preload). That launcher
    /// is the SAME one the council's Tier C (Broker the Compact) will use, and it
    /// is not built yet — so the contract resolves automatically for now. When
    /// the shared interactive-climax launcher lands, this commission becomes its
    /// entry point without changing the effect.</summary>
    public static ShadowMarketResult CommissionAssassination(CycleState cycle, string kingdomId,
                                                            string courtierId)
    {
        int undercroft = SaveManager.ActiveSave != null
            ? CouncilQueries.BuildingTier(SaveManager.ActiveSave, ShadowVocab.BuildingUndercroft) : 0;
        if (undercroft < ShadowVocab.AssassinationMinUndercroft)
        {
            return ShadowMarketResult.Fail(
                $"Assassination needs Undercroft {ShadowVocab.AssassinationMinUndercroft} " +
                $"(at {undercroft}).");
        }
        if (string.IsNullOrEmpty(courtierId))
        {
            return ShadowMarketResult.Fail("Name the mark.");
        }
        return Commission(cycle, ShadowVocab.ContractAssassination, kingdomId, courtierId,
            ShadowVocab.FavorCostAssassination, ShadowVocab.ContractDurAssassination,
            ConcordStandingBand.Inner);
    }

    // ── The outbid (§3e) ─────────────────────────────────────────────────

    /// <summary>Buy back the Astrologer's against-guild contract by beating its
    /// bid with hoarded Favor. The signature late-cycle decision: spend the
    /// reserve to survive, or hold it to strike. Succeeds only with strictly more
    /// Favor than the standing bid.</summary>
    public static ShadowMarketResult Outbid(CycleState cycle)
    {
        var council = cycle?.Council;
        if (council == null)
        {
            return ShadowMarketResult.Fail("No council state.");
        }
        ConcordContract against = null;
        foreach (var c in council.ConcordContracts)
        {
            if (c.AgainstPlayer)
            {
                against = c;
                break;
            }
        }
        if (against == null)
        {
            return ShadowMarketResult.Fail("The shadows hold no contract against the guild.");
        }

        int toBeat = against.FavorPaid + 1;
        if (council.ConcordFavor < toBeat)
        {
            return ShadowMarketResult.Fail(
                $"Not enough favor to outbid ({council.ConcordFavor}/{toBeat}).");
        }

        council.ConcordFavor -= toBeat;
        council.ConcordContracts.Remove(against);
        // Outbidding is defensive — it does NOT deepen Marked. Raising it here
        // would trap the guild above the Contracted-Against line indefinitely;
        // the way out is to stop dealing and let Marked decay below 9.
        SaveManager.MarkDirty();
        return ShadowMarketResult.Pass(
            $"The guild outbids the Astrologer — the contract is bought back for {toBeat} favor " +
            $"({council.ConcordFavor} left, Marked {council.Marked}).");
    }

    /// <summary>Shared commission path: validate contact, standing band, and
    /// Favor; spend the Favor; enqueue the contract. Effects apply on the tick
    /// (ShadowTick.ResolveContracts), where dealings and Marked are also booked
    /// — so a commission that never completes costs Favor but leaves no mark.</summary>
    private static ShadowMarketResult Commission(CycleState cycle, string contractType,
        string kingdomId, string targetId, int cost, int duration, ConcordStandingBand minBand)
    {
        var council = cycle?.Council;
        if (council == null || !council.ConcordContacted)
        {
            return ShadowMarketResult.Fail("The Concord is not yet known to the guild.");
        }

        var band = ShadowVocab.EffectiveBand(council.ConcordContacted, council.ConcordDealings);
        if (band < minBand)
        {
            return ShadowMarketResult.Fail(
                $"The Concord will not sell that yet (need {minBand}, at {band}).");
        }

        // Undercroft contract cap (§6): only guild contracts count; the
        // Astrologer's against-guild work is not the guild's to run.
        int undercroft = SaveManager.ActiveSave != null
            ? CouncilQueries.BuildingTier(SaveManager.ActiveSave, ShadowVocab.BuildingUndercroft) : 0;
        int activeGuild = 0;
        foreach (var c in council.ConcordContracts)
        {
            if (!c.AgainstPlayer) { activeGuild++; }
        }
        if (activeGuild >= ShadowVocab.ContractCap(undercroft))
        {
            return ShadowMarketResult.Fail(
                $"The Undercroft is already running all it can ({activeGuild}/" +
                $"{ShadowVocab.ContractCap(undercroft)} contracts).");
        }

        if (council.ConcordFavor < cost)
        {
            return ShadowMarketResult.Fail(
                $"Not enough favor ({council.ConcordFavor}/{cost}).");
        }

        council.ConcordFavor -= cost;
        council.ConcordContracts.Add(new ConcordContract
        {
            Id = $"contract_{cycle.Calendar.CurrentLunation}_{council.ConcordContracts.Count}",
            ContractType = contractType,
            TargetKingdomId = kingdomId ?? "",
            TargetId = targetId ?? "",
            LunationsRemaining = duration,
            FavorPaid = cost,
            AgainstPlayer = false,
        });
        SaveManager.MarkDirty();
        return ShadowMarketResult.Pass(
            $"Commissioned {contractType} in {kingdomId} for {cost} favor " +
            $"({duration} lunation(s); {council.ConcordFavor} banked).");
    }
}
