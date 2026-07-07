using Godot;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// CouncilTick.cs
//
// Purpose:        The council layer's lunation tick (Court &
//                 Council phase C2) plus its mission catalog and
//                 shared queries. Runs INSIDE the world tick,
//                 BEFORE CorruptionSpread.Tick (§13 order): envoy
//                 residency must be computable from missions that
//                 were still live when the moon turned.
//
//                 C4 report change: the Herald's Report is now a
//                 persisted List<HeraldReport> on CouncilState, not
//                 a session-only static List<string>. Lines carry
//                 KingdomId (court-card echo history) and Lunation
//                 (display grouping). CouncilPanel reads Council.Reports
//                 directly and reconstructs lunation headers for display.
// Layer:          System
// Collaborators:  CouncilState.cs (holds Reports), StrategicView.cs
//                 (calls Tick before CorruptionSpread), CouncilPanel.cs
//                 (dispatch UI + report display), CompanionRoster.cs
//                 (envoy-absence enforcement), WorldData.cs (intel)
// See:            court_council_system_v1_1.docx §5, §6, §8, §13
// ============================================================

/// <summary>One dispatchable mission type. Authored in code for C2
/// (consistent with C1's courtier pools); migrates to JSON later.</summary>
public class CouncilMissionDef
{
    public string Id = "";
    public string DisplayName = "";
    public int Lunations = 1;
    public int GoldCost = 0;
    public bool RequiresContact = false;
    public bool NeedsTargetCourtier = false;
    public string Blurb = "";

    /// <summary>Minimum standing band to dispatch (Unknown = no gate).</summary>
    public CourtStandingBand MinBand = CourtStandingBand.Unknown;

    /// <summary>Embassy tier required to dispatch (0 = no gate).</summary>
    public int RequiredEmbassyTier = 0;
}

/// <summary>The Tier A mission catalog (C2).</summary>
public static class CouncilMissions
{
    public const string AttendCourt = "attend_court";
    public const string PresentGifts = "present_gifts";
    public const string GatherIntelligence = "gather_intelligence";
    public const string PetitionMinor = "petition_minor";
    public const string CourtCourtier = "court_courtier";

    public static readonly List<CouncilMissionDef> All = new()
    {
        new CouncilMissionDef
        {
            Id = AttendCourt, DisplayName = "Attend Court",
            Lunations = 1, GoldCost = 25,
            RequiresContact = false, NeedsTargetCourtier = false,
            Blurb = "Establish or maintain a presence. +1 Regard with the most receptive power at court.",
        },
        new CouncilMissionDef
        {
            Id = PresentGifts, DisplayName = "Present Gifts",
            Lunations = 1, GoldCost = 75,
            RequiresContact = true, NeedsTargetCourtier = true,
            Blurb = "A gift matched to a courtier's tastes. Well-judged: +1 or +2 Regard. Misjudged: an insult.",
        },
        new CouncilMissionDef
        {
            Id = GatherIntelligence, DisplayName = "Gather Intelligence",
            Lunations = 2, GoldCost = 40,
            RequiresContact = true, NeedsTargetCourtier = false,
            Blurb = "Chart the kingdom's ground, uncover its places — and perhaps a courtier's secret. Raises Exposure.",
        },
        new CouncilMissionDef
        {
            Id = PetitionMinor, DisplayName = "Petition (Minor)",
            Lunations = 1, GoldCost = 75,
            RequiresContact = true, NeedsTargetCourtier = true,
            MinBand = CourtStandingBand.Welcome, RequiredEmbassyTier = 1,
            Blurb = "Ask a favor of a receptive power at court. Mints one minor favor owed to the guild.",
        },
        new CouncilMissionDef
        {
            Id = CourtCourtier, DisplayName = "Court a Courtier",
            Lunations = 2, GoldCost = 100,
            RequiresContact = true, NeedsTargetCourtier = true,
            MinBand = CourtStandingBand.Welcome, RequiredEmbassyTier = 1,
            Blurb = "Cultivate a receptive power into a sworn Patron whose name lends weight at the negotiating table in this kingdom's territory. Requires deep personal regard (+2).",
        },
    };

    public static CouncilMissionDef Get(string id)
    {
        foreach (var m in All)
        {
            if (m.Id == id)
            {
                return m;
            }
        }
        return null;
    }
}

/// <summary>Shared read-only queries against the council layer. Envoy
/// status is DERIVED from ActiveMissions — never stored on Companion
/// (single-source rule, same as corruption in CampaignState).</summary>
public static class CouncilQueries
{
    public static bool IsOnMission(string companionId)
    {
        var council = SaveManager.ActiveSave?.Cycle?.Council;
        if (council == null || string.IsNullOrEmpty(companionId))
        {
            return false;
        }
        foreach (var m in council.ActiveMissions)
        {
            if (m.CompanionId == companionId)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>True if the companion is held captive after Imprisonment (§8) —
    /// derived from CouncilState.Imprisoned, never a flag on Companion.</summary>
    public static bool IsImprisoned(string companionId)
    {
        var council = SaveManager.ActiveSave?.Cycle?.Council;
        if (council == null || string.IsNullOrEmpty(companionId))
        {
            return false;
        }
        foreach (var e in council.Imprisoned)
        {
            if (e.CompanionId == companionId)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The active mission at a court, or null.</summary>
    public static EnvoyMission MissionAt(string kingdomId)
    {
        var council = SaveManager.ActiveSave?.Cycle?.Council;
        if (council == null)
        {
            return null;
        }
        foreach (var m in council.ActiveMissions)
        {
            if (m.KingdomId == kingdomId)
            {
                return m;
            }
        }
        return null;
    }

    /// <summary>Tier of any campus building by id (0 if absent).</summary>
    public static int BuildingTier(GuildSaveData save, string buildingId)
    {
        if (save?.Buildings == null)
        {
            return 0;
        }
        foreach (var b in save.Buildings)
        {
            if (b.Id == buildingId)
            {
                return b.Tier;
            }
        }
        return 0;
    }

    /// <summary>Embassy tier from the campus (0 if the building doesn't
    /// exist in the save — the template may not be authored yet).</summary>
    public static int EmbassyTier(GuildSaveData save) => BuildingTier(save, "embassy");

    // ── Standing unification (v1.2 ruling): court standing is the SINGLE
    // source of truth for how a kingdom regards the guild. Stance is a
    // derived read; nothing stores it. ─────────────────────────────────────

    /// <summary>Derived kingdom stance from the court's standing band.
    /// Kingdoms without a court (the convergence) read Hostile.</summary>
    public static KingdomStance StanceFor(CycleState cycle, string kingdomId)
    {
        if (cycle?.Council == null ||
            !cycle.Council.Courts.TryGetValue(kingdomId, out var court))
        {
            return KingdomStance.Hostile; // the convergence, or no court
        }
        return court.Band() switch
        {
            CourtStandingBand.Hostile => KingdomStance.Hostile,
            CourtStandingBand.Welcome => KingdomStance.Friendly,
            CourtStandingBand.Favored => KingdomStance.Friendly,
            CourtStandingBand.Trusted => KingdomStance.Allied,
            _ => KingdomStance.Neutral, // Unknown, Received
        };
    }

    /// <summary>Derived reputation integer for the negotiation system's
    /// starting-tension formula, for KINGDOM-aligned NPCs. Matches the
    /// scale NegotiationState.Initialize expects (>=2 Allied ... <=-2
    /// Hostile). Non-kingdom factions keep FactionReputation.</summary>
    public static int NegotiationReputationFor(CycleState cycle, string kingdomId)
    {
        return StanceFor(cycle, kingdomId) switch
        {
            KingdomStance.Allied => 2,
            KingdomStance.Friendly => 1,
            KingdomStance.Hostile => -2,
            KingdomStance.Unfriendly => -1, // unreachable in v1; future granularity
            _ => 0,
        };
    }

    /// <summary>Concurrent envoy cap: 1 with no Embassy, +1 per tier (§2b).</summary>
    public static int EnvoyCap(GuildSaveData save) => 1 + EmbassyTier(save);

    /// <summary>Total Patron slots across ALL courts (§2b): none without an
    /// Embassy, one at Embassy I, a second at Embassy II. Embassy III's slot
    /// count is UNRULED — held at 2 until decided.</summary>
    public static int PatronSlots(GuildSaveData save)
    {
        int tier = EmbassyTier(save);
        if (tier <= 0)
        {
            return 0;
        }
        return tier >= 2 ? 2 : 1;
    }

    /// <summary>Patrons currently sworn across all courts (scalar per court).</summary>
    public static int PatronsUsed(CycleState cycle)
    {
        if (cycle?.Council == null)
        {
            return 0;
        }
        int used = 0;
        foreach (var c in cycle.Council.Courts.Values)
        {
            if (!string.IsNullOrEmpty(c.PatronCourtierId))
            {
                used++;
            }
        }
        return used;
    }
}

/// <summary>The per-lunation council resolution. The Herald's Report is now
/// persisted on CouncilState.Reports (cycle tier); this class no longer owns
/// report state, only writes to it.</summary>
public static class CouncilTick
{
    private const int MaxReportLines = 60;

    // Exposure thresholds + consequences (§8). Edge-triggered on upward
    // crossing; exposure is NOT reset on fire (v1.2 latch ruling — reset
    // semantics capped reachable exposure at 6, making Expulsion/Imprisonment
    // impossible; doc erratum).
    private const int ScandalThreshold = 4;
    private const int ExpulsionThreshold = 7;
    private const int ImprisonmentThreshold = 10;
    private const int ScandalStandingPenalty = 4;
    private const int ExpulsionFreezeLunations = 2;

    /// <summary>Run one lunation of council resolution. Call from the
    /// lunation-boundary branch in StrategicView.Deploy, BEFORE
    /// CorruptionSpread.Tick.</summary>
    public static void Tick(CycleState cycle)
    {
        if (cycle?.Council == null)
        {
            return;
        }
        var council = cycle.Council;
        int lun = cycle.Calendar.CurrentLunation;
        var reports = new List<HeraldReport>();

        // Snapshot per-court exposure BEFORE mission resolution (step 4) mutates
        // it, so step 5 can detect UPWARD threshold crossings (edge-triggered):
        // a consequence fires when exposure moves from below a line to at/above
        // it this tick. No persisted "already fired" field is needed — the
        // exposure value itself is the memory. Re-firing a tier requires the
        // court to decay below the line and then be pushed back across it.
        var exposureBefore = new Dictionary<string, int>();
        foreach (var court in council.Courts.Values)
        {
            exposureBefore[court.KingdomId] = court.Exposure;
        }

        // Which courts ran an intelligence-class mission this lunation — those
        // courts skip idle exposure decay (§13 step 5). NOTE (C5): when Rumor /
        // Discredit missions land, add them to this set too, or their exposure
        // gain is decayed away the same tick and never crosses a threshold.
        var intelCourts = new HashSet<string>();
        foreach (var m in council.ActiveMissions)
        {
            if (m.MissionType == CouncilMissions.GatherIntelligence && !m.Recalled)
            {
                intelCourts.Add(m.KingdomId);
            }
        }

        // ── Step 1: land echoes whose lunation has come (§13) ────────────
        // Echoes carry per-court attribution: each landed / dissipated /
        // buried line is tagged with its target kingdom (echo.KingdomId) so
        // court-card echo history can filter on it. LandEchoes appends
        // HeraldReports directly — no guild-wide bridge.
        CouncilEcho.LandEchoes(cycle, reports);

        // ── Step 2: obligation decay on overdue favors the guild owes ────
        CouncilLedger.TickObligationDecay(cycle, reports);

        // ── Step 4: resolve / advance missions ───────────────────────────
        // Iterate a copy: resolution removes entries. Record which envoy resolved
        // at each court this tick (transient, rebuilt each tick like exposureBefore)
        // so step-5 consequences can name/seize the caught envoy — the mission is
        // removed here, BEFORE step 5, so MissionAt can no longer find them.
        var resolverThisTick = new Dictionary<string, string>();
        foreach (var mission in council.ActiveMissions.ToList())
        {
            mission.LunationsRemaining -= 1;
            if (mission.LunationsRemaining > 0)
            {
                continue;
            }

            council.ActiveMissions.Remove(mission);
            resolverThisTick[mission.KingdomId] = mission.CompanionId;
            ResolveMission(cycle, mission, reports);
        }

        // ── Step 5: exposure decay, freeze decrement, threshold consequences ─
        foreach (var court in council.Courts.Values)
        {
            if (court.MissionFreezeLunations > 0)
            {
                court.MissionFreezeLunations -= 1;
            }
            if (court.Exposure > 0 && !intelCourts.Contains(court.KingdomId))
            {
                court.Exposure -= 1;
            }

            int before = exposureBefore.TryGetValue(court.KingdomId, out var b) ? b : 0;
            CheckExposureThresholds(cycle, court, before, court.Exposure, lun, reports, resolverThisTick);
        }

        // ── Step 8: append to the persisted report and trim ──────────────
        if (reports.Count > 0)
        {
            foreach (var r in reports)
            {
                council.Reports.Add(r);
                GD.Print($"[Herald] L{r.Lunation} {r.Text}");
            }
            while (council.Reports.Count > MaxReportLines)
            {
                council.Reports.RemoveAt(0);
            }
            SaveManager.MarkDirty();
        }
    }

    // ── Mission resolution ────────────────────────────────────────────────

    private static void ResolveMission(CycleState cycle, EnvoyMission mission,
                                       List<HeraldReport> reports)
    {
        var council = cycle.Council;
        int lun = cycle.Calendar.CurrentLunation;
        if (!council.Courts.TryGetValue(mission.KingdomId, out var court))
        {
            return; // court vanished (should be impossible); mission simply ends
        }

        var envoy = cycle.Companions.Find(c => c.Id == mission.CompanionId);
        string envoyName = envoy?.Name ?? mission.CompanionId;
        string courtName = CourtDisplayName(cycle, mission.KingdomId);

        if (mission.Recalled)
        {
            Emit(reports, lun, mission.KingdomId,
                $"{envoyName} returns early from {courtName}. Nothing was gained.");
            return;
        }

        switch (mission.MissionType)
        {
            case CouncilMissions.AttendCourt:
                ResolveAttendCourt(court, lun, envoyName, courtName, reports);
                break;
            case CouncilMissions.PresentGifts:
                ResolvePresentGifts(court, lun, mission, envoy, envoyName, courtName, reports);
                break;
            case CouncilMissions.GatherIntelligence:
                ResolveGatherIntelligence(cycle, court, lun, envoy, envoyName, courtName, reports);
                break;
            case CouncilMissions.PetitionMinor:
                ResolvePetition(cycle, court, lun, mission, envoyName, courtName, reports);
                break;
            case CouncilMissions.CourtCourtier:
                ResolveCourtship(cycle, court, lun, mission, envoyName, courtName, reports);
                break;
        }

        SaveManager.MarkDirty();
    }

    private static void ResolveAttendCourt(CourtState court, int lun, string envoyName,
                                           string courtName, List<HeraldReport> reports)
    {
        bool firstContact = !court.HasContact;
        court.HasContact = true;

        // Highest-Influence receptive courtier (Regard > -3); ties broken by
        // higher Regard, then list order. Post-clamp courts always have one.
        CourtierState target = null;
        foreach (var c in court.Courtiers)
        {
            if (c.Regard <= -3)
            {
                continue;
            }
            if (target == null ||
                c.Influence > target.Influence ||
                (c.Influence == target.Influence && c.Regard > target.Regard))
            {
                target = c;
            }
        }
        if (target == null)
        {
            Emit(reports, lun, court.KingdomId,
                $"{envoyName} attended {courtName}, but found no willing ear.");
            return;
        }

        target.Regard = Mathf.Clamp(target.Regard + 1, -3, 3);
        string opener = firstContact
            ? $"{envoyName} has been received at {courtName} for the first time."
            : $"{envoyName} attended {courtName}.";
        Emit(reports, lun, court.KingdomId,
            $"{opener} {FirstName(target.DisplayName)} the {OfficeDisplay(target.Office)} " +
            $"warms to the guild (Regard {Signed(target.Regard)}). " +
            $"Standing: {court.Band()}.");
    }

    private static void ResolvePresentGifts(CourtState court, int lun, EnvoyMission mission,
        Companion envoy, string envoyName, string courtName, List<HeraldReport> reports)
    {
        var target = court.GetCourtier(mission.TargetCourtierId);
        if (target == null)
        {
            Emit(reports, lun, court.KingdomId,
                $"{envoyName}'s gift found no recipient at {courtName}.");
            return;
        }
        court.HasContact = true;

        // Match-quality roll, shifted by envoy fitness. School-vs-archmage
        // modifiers wait on ArchmageDefinition exposing a school field.
        int roll = (int)(GD.Randi() % 100) + 15 * FitnessMod(envoy);
        int delta;
        string verdict;
        if (roll < 20)
        {
            delta = -1;
            verdict = "the gift missed its mark — an insult taken";
        }
        else if (roll < 70)
        {
            delta = 1;
            verdict = "the gift was well received";
        }
        else
        {
            delta = 2;
            verdict = "the gift was perfectly judged";
        }

        target.Regard = Mathf.Clamp(target.Regard + delta, -3, 3);
        Emit(reports, lun, court.KingdomId,
            $"{envoyName} presented gifts to {FirstName(target.DisplayName)} " +
            $"the {OfficeDisplay(target.Office)} at {courtName}: {verdict} " +
            $"(Regard {Signed(target.Regard)}). Standing: {court.Band()}.");
    }

    private static void ResolveGatherIntelligence(CycleState cycle, CourtState court, int lun,
        Companion envoy, string envoyName, string courtName, List<HeraldReport> reports)
    {
        court.HasContact = true;
        var world = cycle.World;

        // Reveal up to 2 undiscovered POIs in the kingdom and chart the
        // ground around them (Unseen -> Charted; never downgrades Explored).
        int revealed = 0, charted = 0;
        foreach (var poi in world.Pois)
        {
            if (revealed >= 2)
            {
                break;
            }
            if (poi.KingdomId != court.KingdomId || poi.Discovered)
            {
                continue;
            }
            poi.Discovered = true;
            revealed++;

            for (int dy = -3; dy <= 3; dy++)
            {
                for (int dx = -3; dx <= 3; dx++)
                {
                    int x = poi.X + dx, y = poi.Y + dy;
                    if (!world.InBounds(x, y))
                    {
                        continue;
                    }
                    int idx = y * world.Width + x;
                    if (world.Tiles[idx].Discovery == TileDiscovery.Unseen)
                    {
                        world.Tiles[idx].Discovery = TileDiscovery.Charted;
                        charted++;
                    }
                }
            }
        }

        // Secret discovery roll (the mission's second lunation of work).
        CourtierState secretHolder = null;
        int roll = (int)(GD.Randi() % 100) + 15 * FitnessMod(envoy);
        bool secretFound = roll >= 25; // 75% base
        if (secretFound)
        {
            foreach (var c in court.Courtiers)
            {
                if (!c.SecretKnown)
                {
                    secretHolder = c;
                    c.SecretKnown = true;
                    break;
                }
            }
        }

        // Exposure: +1 on a clean job, +2 when the digging got noticed (§13).
        court.Exposure = Mathf.Clamp(court.Exposure + (secretFound ? 1 : 2), 0, 10);

        string intel = revealed > 0
            ? $"charted {charted} tiles and located {revealed} site(s)"
            : "found little ground left to chart";
        string secret = secretHolder != null
            ? $" A secret of {FirstName(secretHolder.DisplayName)} the {OfficeDisplay(secretHolder.Office)} is now known to the guild."
            : (secretFound ? "" : " The court's secrets stayed buried, and questions were asked.");
        Emit(reports, lun, court.KingdomId,
            $"{envoyName} worked the shadows of {courtName}: {intel}.{secret} " +
            $"(Exposure {court.Exposure}/10.)");
    }

    private static void ResolvePetition(CycleState cycle, CourtState court, int lun,
        EnvoyMission mission, string envoyName, string courtName, List<HeraldReport> reports)
    {
        court.HasContact = true;

        // Resolution-time backstop for the Welcome gate — covers standing
        // dropping mid-mission and any un-gated dispatch path.
        if (court.Band() < CourtStandingBand.Welcome)
        {
            Emit(reports, lun, court.KingdomId,
                $"{envoyName}'s petition at {courtName} was heard politely and " +
                $"declined — the guild's standing does not yet command favors.");
            return;
        }

        var target = court.GetCourtier(mission.TargetCourtierId);
        if (!CouncilLedger.IsReceptive(target) ||
            !CouncilLedger.IsPetitionableOffice(target.Office))
        {
            Emit(reports, lun, court.KingdomId,
                $"{envoyName}'s petition at {courtName} found no willing patron.");
            return;
        }

        var favor = CouncilLedger.MintPetitionFavor(cycle, court, target,
            $"Petitioned of {target.DisplayName} the {OfficeDisplay(target.Office)} at {courtName}");
        Emit(reports, lun, court.KingdomId,
            $"{envoyName} secured a favor at {courtName}: {favor.Type} (minor), " +
            $"owed by {FirstName(target.DisplayName)} the {OfficeDisplay(target.Office)}.");
    }

    /// <summary>Court a Courtier (C5, automated portion). Cultivates a courtier
    /// of sufficient personal regard into the court's sworn Patron, setting
    /// CourtState.PatronCourtierId (read by NegotiationManager to grant a
    /// Connections token in this kingdom's negotiations). Gated by a Regard
    /// floor (+2), the court's single Patron seat, and the guild's global
    /// Patron slots (Embassy-derived, §2b). All refusals are no-ops with an
    /// attributed line — consistent with C3 call-in refusal semantics.</summary>
    private static void ResolveCourtship(CycleState cycle, CourtState court, int lun,
        EnvoyMission mission, string envoyName, string courtName, List<HeraldReport> reports)
    {
        court.HasContact = true;

        var target = court.GetCourtier(mission.TargetCourtierId);
        if (target == null)
        {
            Emit(reports, lun, court.KingdomId,
                $"{envoyName}'s courtship at {courtName} found no one to court.");
            return;
        }

        // Already sworn here — nothing to win twice.
        if (court.PatronCourtierId == target.Id)
        {
            Emit(reports, lun, court.KingdomId,
                $"{FirstName(target.DisplayName)} the {OfficeDisplay(target.Office)} is already " +
                $"sworn to the guild at {courtName}.");
            return;
        }

        // The court's single Patron seat is held by someone else.
        if (!string.IsNullOrEmpty(court.PatronCourtierId))
        {
            var held = court.GetCourtier(court.PatronCourtierId);
            string heldName = held != null
                ? $"{FirstName(held.DisplayName)} the {OfficeDisplay(held.Office)}"
                : "another";
            Emit(reports, lun, court.KingdomId,
                $"{envoyName} courted at {courtName}, but the guild's patron there is already " +
                $"{heldName}. A court answers to one sworn friend of the guild.");
            return;
        }

        // Relationship floor: the oath is sworn from deep regard, not bought cold.
        const int PatronRegardFloor = 2;
        if (target.Regard < PatronRegardFloor)
        {
            Emit(reports, lun, court.KingdomId,
                $"{envoyName} courted {FirstName(target.DisplayName)} the {OfficeDisplay(target.Office)} " +
                $"at {courtName}, but regard is not yet deep enough for an oath " +
                $"(Regard {Signed(target.Regard)}; needs {Signed(PatronRegardFloor)}).");
            return;
        }

        // Global Patron slots (§2b, Embassy-gated). This court contributes 0 to
        // the count here because its seat is provably empty (checked above).
        int slots = CouncilQueries.PatronSlots(SaveManager.ActiveSave);
        int used = CouncilQueries.PatronsUsed(cycle);
        if (used >= slots)
        {
            Emit(reports, lun, court.KingdomId,
                $"{envoyName} won the friendship of {FirstName(target.DisplayName)} the " +
                $"{OfficeDisplay(target.Office)} at {courtName}, but the guild has no Patron seat " +
                $"to offer ({used}/{slots} sworn). Expand the Embassy to take on another.");
            return;
        }

        // Swear the oath; the courtship deepens the bond to its peak.
        court.PatronCourtierId = target.Id;
        target.Regard = Mathf.Clamp(target.Regard + 1, -3, 3);
        Emit(reports, lun, court.KingdomId,
            $"{FirstName(target.DisplayName)} the {OfficeDisplay(target.Office)} is now sworn Patron " +
            $"of the guild at {courtName} (Regard {Signed(target.Regard)}). Their name will lend " +
            $"weight at the table in {courtName}'s territory.");
    }

    // ── Exposure thresholds (§8) ──────────────────────────────────────────

    /// <summary>Fire every exposure threshold the court crossed upward this
    /// tick (Scandal, Expulsion; Imprisonment deferred), ascending. Downward
    /// or flat movement fires nothing.</summary>
    private static void CheckExposureThresholds(CycleState cycle, CourtState court,
        int before, int after, int lun, List<HeraldReport> reports,
        Dictionary<string, string> resolvers)
    {
        if (after <= before)
        {
            return;
        }
        // The envoy who resolved a mission here this tick — the one caught. Null if
        // the crossing came from some non-mission source (none exist today).
        string caughtId = resolvers.TryGetValue(court.KingdomId, out var cid) ? cid : null;
        if (Crossed(before, after, ScandalThreshold))
        {
            FireScandal(cycle, court, lun, caughtId, reports);
        }
        if (Crossed(before, after, ExpulsionThreshold))
        {
            FireExpulsion(cycle, court, lun, caughtId, reports);
        }
        if (Crossed(before, after, ImprisonmentThreshold))
        {
            FireImprisonment(cycle, court, lun, caughtId, reports);
        }
    }

    /// <summary>True if exposure moved from strictly below a threshold to at or
    /// above it this tick.</summary>
    private static bool Crossed(int before, int after, int threshold)
        => before < threshold && after >= threshold;

    /// <summary>Scandal (§8): a lasting standing penalty and a report naming both
    /// who caught the scent and the envoy who slinks home in disgrace. The
    /// mission has already resolved (envoy returns to the pool); Scandal does not
    /// hold them — only Imprisonment does.</summary>
    private static void FireScandal(CycleState cycle, CourtState court, int lun,
        string caughtId, List<HeraldReport> reports)
    {
        court.StandingPenalty += ScandalStandingPenalty;

        string envoyClause = "";
        if (!string.IsNullOrEmpty(caughtId))
        {
            var envoy = cycle.Companions.Find(c => c.Id == caughtId);
            envoyClause = $" {envoy?.Name ?? caughtId} slinks home in disgrace.";
        }

        var catcher = ScentCatcher(court);
        string catcherClause = catcher != null
            ? $"{catcher.DisplayName} the {OfficeDisplay(catcher.Office)} caught the scent"
            : "the court caught the scent";

        Emit(reports, lun, court.KingdomId,
            $"SCANDAL at {CourtDisplayName(cycle, court.KingdomId)}: {catcherClause}. " +
            $"The guild's standing suffers (-{ScandalStandingPenalty}).{envoyClause} " +
            $"Standing: {court.Band()}.");
    }

    /// <summary>Expulsion (§8): the court casts the guild out — missions frozen
    /// and standing capped at Received (via CourtState.Band) for the freeze. Names
    /// the envoy expelled alongside the guild.</summary>
    private static void FireExpulsion(CycleState cycle, CourtState court, int lun,
        string caughtId, List<HeraldReport> reports)
    {
        court.MissionFreezeLunations = ExpulsionFreezeLunations;

        string envoyClause = "";
        if (!string.IsNullOrEmpty(caughtId))
        {
            var envoy = cycle.Companions.Find(c => c.Id == caughtId);
            envoyClause = $" {envoy?.Name ?? caughtId} is marched to the border.";
        }

        Emit(reports, lun, court.KingdomId,
            $"EXPULSION from {CourtDisplayName(cycle, court.KingdomId)}: the guild is cast out.{envoyClause} " +
            $"No envoys may be sent for {ExpulsionFreezeLunations} lunations, and standing is " +
            $"capped at Received until the doors reopen.");
    }

    /// <summary>Imprisonment (§8): the caught envoy is seized. They are held via
    /// CouncilState.Imprisoned (blocked from the party by the derived TryAddToParty
    /// guard) until a rescue Prison POI — sited near the kingdom's seat — is
    /// stormed and won (ExpeditionManager.ReleaseImprisonedAt). If no gaol can be
    /// sited the captive would be unrecoverable, so they flee instead — no
    /// soft-lock. If no envoy resolved here this tick, only a report fires.</summary>
    private static void FireImprisonment(CycleState cycle, CourtState court, int lun,
        string caughtId, List<HeraldReport> reports)
    {
        court.MissionFreezeLunations = ExpulsionFreezeLunations; // cast out as well

        if (string.IsNullOrEmpty(caughtId))
        {
            Emit(reports, lun, court.KingdomId,
                $"The guild's intrigues at {CourtDisplayName(cycle, court.KingdomId)} reached a " +
                $"breaking point, but no envoy was on hand to seize.");
            return;
        }

        var envoy = cycle.Companions.Find(c => c.Id == caughtId);
        string envoyName = envoy?.Name ?? caughtId;

        if (CouncilQueries.IsImprisoned(caughtId))
        {
            return; // already held elsewhere — defensive, shouldn't recur
        }

        int poiIndex = cycle.World != null
            ? WorldGenerator.SiteRuntimePoi(cycle.World, PoiKind.Prison, court.KingdomId)
            : -1;
        if (poiIndex < 0)
        {
            // No gaol could be sited (no seat, or no free tile). Without a rescue
            // POI the captive would be unrecoverable, so they slip away instead.
            Emit(reports, lun, court.KingdomId,
                $"IMPRISONMENT loomed at {CourtDisplayName(cycle, court.KingdomId)}, but {envoyName} " +
                $"slipped the noose and fled home. The guild's welcome there is spent all the same.");
            return;
        }

        cycle.Council.Imprisoned.Add(new ImprisonedEnvoy
        {
            CompanionId = caughtId,
            KingdomId = court.KingdomId,
            PrisonPoiIndex = poiIndex,
            LunationImprisoned = lun,
        });
        SaveManager.MarkDirty();

        Emit(reports, lun, court.KingdomId,
            $"IMPRISONMENT at {CourtDisplayName(cycle, court.KingdomId)}: {envoyName} is seized and " +
            $"cast into a gaol near the kingdom's seat. Storm it to bring them home.");
    }

    /// <summary>Who catches the intrigue: the Spymaster if the court has one,
    /// else the highest-Influence courtier.</summary>
    private static CourtierState ScentCatcher(CourtState court)
    {
        CourtierState best = null;
        foreach (var c in court.Courtiers)
        {
            if (c.Office == CourtVocab.OfficeSpymaster)
            {
                return c;
            }
            if (best == null || c.Influence > best.Influence)
            {
                best = c;
            }
        }
        return best;
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>Append one attributed report line. KingdomId "" = guild-wide.</summary>
    private static void Emit(List<HeraldReport> reports, int lunation,
                             string kingdomId, string text)
    {
        reports.Add(new HeraldReport
        {
            Lunation = lunation,
            KingdomId = kingdomId ?? "",
            Text = text,
        });
    }

    /// <summary>Envoy fitness modifier. C2: completed arc only. School-vs-
    /// archmage terms join when ArchmageDefinition exposes a school; the
    /// archetype-matchup term joins when the negotiation token mapping is
    /// shared or promoted to companion data (§2a).</summary>
    private static int FitnessMod(Companion envoy)
        => (envoy != null && envoy.ArcStage >= 4) ? 1 : 0;

    public static string CourtDisplayName(CycleState cycle, string kingdomId)
    {
        if (cycle.Kingdoms.TryGetValue(kingdomId, out var ks) &&
            !string.IsNullOrEmpty(ks.TemplateRegionId))
        {
            return Prettify(ks.TemplateRegionId);
        }
        return Prettify(kingdomId);
    }

    /// <summary>Insert spaces into CamelCase office ids for display
    /// ("CourtWizard" -> "Court Wizard"). The id itself stays CamelCase.</summary>
    public static string OfficeDisplay(string office)
    {
        if (string.IsNullOrEmpty(office))
        {
            return "";
        }
        var sb = new System.Text.StringBuilder(office.Length + 2);
        for (int i = 0; i < office.Length; i++)
        {
            if (i > 0 && char.IsUpper(office[i]) && char.IsLower(office[i - 1]))
            {
                sb.Append(' ');
            }
            sb.Append(office[i]);
        }
        return sb.ToString();
    }

    public static string Prettify(string id)
    {
        var parts = id.Split('_');
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length > 0)
            {
                parts[i] = char.ToUpperInvariant(parts[i][0]) + parts[i].Substring(1);
            }
        }
        return string.Join(" ", parts);
    }

    private static string FirstName(string display)
    {
        int sp = display.IndexOf(' ');
        return sp > 0 ? display.Substring(0, sp) : display;
    }

    private static string Signed(int v) => v > 0 ? $"+{v}" : v.ToString();
}
