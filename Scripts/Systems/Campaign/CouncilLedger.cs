using Godot;
using System.Collections.Generic;

// ============================================================
// CouncilLedger.cs
//
// Purpose:        All C3 favor-ledger logic in one place: minting
//                 (Petition resolution), consumption (expedition
//                 call-ins), the office -> favor-type mapping,
//                 petition target validity, and obligation decay
//                 (§13 step 2). Centralized here so CouncilTick
//                 and CouncilPanel each need only one-line hooks.
//
//                 V1 scope notes:
//                   - Callable types are Military / Economic /
//                     Intelligence / Passage. Arcane favors have
//                     no field effect yet (identify / uncurse /
//                     reroll hooks don't exist), so Court Wizard
//                     and Favorite are NOT petitionable in v1.
//                   - Chancellor petitions mint PASSAGE favors in
//                     v1; the Political echo-cancel call-in waits
//                     for C4 (echoes don't exist yet). When C4
//                     lands, Chancellor petitions gain a
//                     Political/Passage choice.
//                   - Obligation decay is live logic but dormant:
//                     nothing mints guild-owed favors until the
//                     negotiation hidden-term wiring / C4 demand
//                     events. DebugMintTestFavors exists so the
//                     decay path can be verified now.
// Layer:          System
// Collaborators:  CouncilState.cs (Favor / CourtState / Courtier),
//                 CouncilTick.cs (Petition resolution + decay call),
//                 CouncilPanel.cs (petition target filter),
//                 LedgerPanel.cs + ExpeditionManager.cs (call-ins),
//                 CalendarState (CurrentLunation for mint/decay age)
// See:            court_council_system_v1_1.docx §4, §4a, §13 step 2
// ============================================================

/// <summary>Static favor-ledger operations for the Court &amp; Council layer
/// (phase C3). Stateless; all state lives in CycleState.Council.Ledger.</summary>
public static class CouncilLedger
{
    // ── Tuning (§13; playtest starting points) ───────────────────────────

    /// <summary>Lunations a guild-owed favor may sit unpaid before festering.</summary>
    public const int ObligationGraceLunations = 3;

    /// <summary>Once festering, -1 Regard with the creditor every this many
    /// lunations. First hit lands at age Grace + Period (5), then 7, 9...</summary>
    public const int ObligationDecayPeriod = 2;

    /// <summary>Favor types with a working expedition call-in effect in C3.</summary>
    public static readonly HashSet<string> CallableTypes = new()
    {
        "Military", "Economic", "Intelligence", "Passage",
    };

    // ── Office -> favor type ─────────────────────────────────────────────

    /// <summary>The favor type a courtier's office can mint (§4a table).
    /// Normalizes spacing/case so "Court Wizard" vs "CourtWizard" vs
    /// "court_wizard" all resolve. Returns "" for offices with no favor
    /// column (Favorite) or unknown strings.</summary>
    public static string OfficeToFavorType(string office)
    {
        string key = (office ?? "").Replace(" ", "").Replace("_", "").ToLowerInvariant();
        switch (key)
        {
            case "marshal":
                return "Military";
            case "steward":
                return "Economic";
            case "spymaster":
                return "Intelligence";
            case "chancellor":
                return "Passage"; // v1: Political call-in waits for C4 echoes
            case "courtwizard":
                return "Arcane";  // mintable type exists; NOT petitionable in v1
            default:
                return "";
        }
    }

    /// <summary>True if petitioning this office yields a favor that can
    /// actually be called in (v1: excludes Court Wizard and Favorite).</summary>
    public static bool IsPetitionableOffice(string office)
        => CallableTypes.Contains(OfficeToFavorType(office));

    /// <summary>Receptive = willing to deal (Regard >= 0). Must match the
    /// receptivity definition used by CouncilTick's Attend Court resolution;
    /// if that helper differs, align this one line.</summary>
    public static bool IsReceptive(CourtierState c) => c != null && c.Regard >= 0;

    /// <summary>Valid petition targets at a court: receptive courtiers whose
    /// office mints a callable favor type. Used by the dispatch UI filter
    /// and re-validated at resolution time.</summary>
    public static List<CourtierState> PetitionTargets(CourtState court)
    {
        var result = new List<CourtierState>();
        if (court == null)
        {
            return result;
        }
        foreach (var c in court.Courtiers)
        {
            if (IsReceptive(c) && IsPetitionableOffice(c.Office))
            {
                result.Add(c);
            }
        }
        return result;
    }

    // ── Mint / consume ───────────────────────────────────────────────────

    /// <summary>Mint one minor favor owed to the guild from a petitioned
    /// courtier. Adds it to the ledger and marks the save dirty.</summary>
    public static Favor MintPetitionFavor(CycleState cycle, CourtState court,
        CourtierState creditor, string sourceDescription)
    {
        var favor = new Favor
        {
            Id = System.Guid.NewGuid().ToString("N"),
            OwedToGuild = true,
            KingdomId = court.KingdomId,
            CourtierId = creditor.Id,
            Type = OfficeToFavorType(creditor.Office),
            IsMajor = false,
            SourceDescription = sourceDescription,
            LunationMinted = cycle.Calendar.CurrentLunation,
        };
        cycle.Council.Ledger.Add(favor);
        SaveManager.MarkDirty();
        return favor;
    }

    /// <summary>Remove a called-in (or repaid) favor from the ledger.</summary>
    public static void Consume(CouncilState council, Favor favor)
    {
        if (council == null || favor == null)
        {
            return;
        }
        council.Ledger.Remove(favor);
        SaveManager.MarkDirty();
    }

    // ── Obligation decay (§13 step 2) ────────────────────────────────────

    /// <summary>Fester overdue favors the GUILD owes: after the grace window,
    /// -1 Regard with the creditor courtier every ObligationDecayPeriod
    /// lunations. Called from CouncilTick.Tick before mission resolution.
    /// Appends attributed lines for the Herald's Report.</summary>
    public static void TickObligationDecay(CycleState cycle, List<string> lines)
    {
        var council = cycle?.Council;
        if (council == null || council.Ledger.Count == 0)
        {
            return;
        }

        int now = cycle.Calendar.CurrentLunation;
        int firstHit = ObligationGraceLunations + ObligationDecayPeriod; // age 5

        foreach (var favor in council.Ledger)
        {
            if (favor.OwedToGuild)
            {
                continue; // only debts the guild carries fester
            }

            int age = now - favor.LunationMinted;
            if (age < firstHit || (age - firstHit) % ObligationDecayPeriod != 0)
            {
                continue;
            }

            if (!council.Courts.TryGetValue(favor.KingdomId, out var court))
            {
                continue;
            }
            var creditor = court.GetCourtier(favor.CourtierId);
            if (creditor == null)
            {
                continue;
            }

            creditor.Regard = Mathf.Clamp(creditor.Regard - 1, -3, 3);
            lines.Add($"An unpaid debt festers at {favor.KingdomId}: " +
                      $"{creditor.DisplayName} the {creditor.Office} grows colder " +
                      $"(Regard {creditor.Regard}). [{favor.SourceDescription}]");
        }
    }

    // ── Debug ────────────────────────────────────────────────────────────

    /// <summary>DEBUG: mint one of each callable minor favor owed to the
    /// guild at the given kingdom, plus one guild-owed favor pre-aged past
    /// the grace window so obligation decay fires on the next tick. Caller
    /// must gate on PlayerSession.DebugMode. Wire from any convenient debug
    /// hook (e.g. a key handler on the strategic view).</summary>
    public static void DebugMintTestFavors(CycleState cycle, string kingdomId)
    {
        if (cycle?.Council == null ||
            !cycle.Council.Courts.TryGetValue(kingdomId, out var court) ||
            court.Courtiers.Count == 0)
        {
            GD.Print($"[CouncilLedger] DEBUG: no court for '{kingdomId}'.");
            return;
        }

        int now = cycle.Calendar.CurrentLunation;
        foreach (string type in CallableTypes)
        {
            cycle.Council.Ledger.Add(new Favor
            {
                Id = System.Guid.NewGuid().ToString("N"),
                OwedToGuild = true,
                KingdomId = kingdomId,
                CourtierId = court.Courtiers[0].Id,
                Type = type,
                IsMajor = false,
                SourceDescription = "[DEBUG] Test favor",
                LunationMinted = now,
            });
        }

        // One the guild owes, aged so decay fires on the very next tick.
        cycle.Council.Ledger.Add(new Favor
        {
            Id = System.Guid.NewGuid().ToString("N"),
            OwedToGuild = false,
            KingdomId = kingdomId,
            CourtierId = court.Courtiers[0].Id,
            Type = "Economic",
            IsMajor = false,
            SourceDescription = "[DEBUG] Overdue test debt",
            LunationMinted = now - (ObligationGraceLunations + ObligationDecayPeriod),
        });

        SaveManager.MarkDirty();
        GD.Print($"[CouncilLedger] DEBUG: minted {CallableTypes.Count} callable " +
                 $"favors + 1 overdue debt at '{kingdomId}'.");
    }
}
