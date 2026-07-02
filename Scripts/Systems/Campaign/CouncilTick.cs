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
//                 C2 scope: Tier A mission resolution (Attend
//                 Court / Present Gifts / Gather Intelligence),
//                 exposure accrual + idle decay, expulsion-freeze
//                 decrement, and Herald's Report v1 (session
//                 memory + log). Echo landing, obligation decay,
//                 agent whispers, and exposure THRESHOLDS are
//                 later phases (C3-C6) — nothing here can create
//                 those objects yet, so no code pretends to.
// Layer:          System
// Collaborators:  CouncilState.cs (the data it mutates),
//                 StrategicView.cs (calls Tick on the lunation
//                 boundary, before CorruptionSpread),
//                 CouncilPanel.cs (dispatch UI + report display),
//                 CompanionRoster.cs (envoy-absence enforcement),
//                 WorldData.cs (intel charting effects)
// See:            court_council_system_v1_1.docx §5, §6, §13
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

    /// <summary>Embassy tier from the campus (0 if the building doesn't
    /// exist in the save — the template may not be authored yet).</summary>
    public static int EmbassyTier(GuildSaveData save)
    {
        if (save?.Buildings == null)
        {
            return 0;
        }
        foreach (var b in save.Buildings)
        {
            if (b.Id == "embassy")
            {
                return b.Tier;
            }
        }
        return 0;
    }

    /// <summary>Concurrent envoy cap: 1 with no Embassy, +1 per tier (§2b).</summary>
    public static int EnvoyCap(GuildSaveData save) => 1 + EmbassyTier(save);
}

/// <summary>The per-lunation council resolution. Stateless except the
/// session-scoped Herald's Report memory (deliberately NOT saved in C2;
/// the persisted, attributed report ships with Word Spreads in C4).</summary>
public static class CouncilTick
{
    private const int MaxReportLines = 60;

    /// <summary>Herald's Report lines, newest last. Session memory only.</summary>
    public static readonly List<string> RecentReports = new();

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
        var lines = new List<string>();

        // Which courts ran an intelligence-class mission this lunation —
        // those courts skip idle exposure decay (§13 step 5).
        var intelCourts = new HashSet<string>();
        foreach (var m in council.ActiveMissions)
        {
            if (m.MissionType == CouncilMissions.GatherIntelligence && !m.Recalled)
            {
                intelCourts.Add(m.KingdomId);
            }
        }

        // ── Step 2: obligation decay on overdue favors the guild owes ────
        CouncilLedger.TickObligationDecay(cycle, lines);

        // ── Step 4: resolve / advance missions ───────────────────────────
        // Iterate a copy: resolution removes entries.
        foreach (var mission in council.ActiveMissions.ToList())
        {
            mission.LunationsRemaining -= 1;
            if (mission.LunationsRemaining > 0)
            {
                continue;
            }

            council.ActiveMissions.Remove(mission);
            ResolveMission(cycle, mission, lines);
        }

        // ── Step 5 (partial): exposure idle decay + freeze decrement ─────
        // Thresholds (Scandal/Expulsion/Imprisonment) are C5.
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
        }

        // ── Step 8 (v1): compile the report ──────────────────────────────
        if (lines.Count > 0)
        {
            RecentReports.Add($"— Lunation {cycle.Calendar.CurrentLunation} —");
            foreach (var line in lines)
            {
                RecentReports.Add(line);
                GD.Print($"[Herald] {line}");
            }
            while (RecentReports.Count > MaxReportLines)
            {
                RecentReports.RemoveAt(0);
            }
            SaveManager.MarkDirty();
        }
    }

    // ── Mission resolution ────────────────────────────────────────────────

    private static void ResolveMission(CycleState cycle, EnvoyMission mission,
                                       List<string> lines)
    {
        var council = cycle.Council;
        if (!council.Courts.TryGetValue(mission.KingdomId, out var court))
        {
            return; // court vanished (should be impossible); mission simply ends
        }

        var envoy = cycle.Companions.Find(c => c.Id == mission.CompanionId);
        string envoyName = envoy?.Name ?? mission.CompanionId;
        string courtName = CourtDisplayName(cycle, mission.KingdomId);

        if (mission.Recalled)
        {
            lines.Add($"{envoyName} returns early from {courtName}. Nothing was gained.");
            return;
        }

        switch (mission.MissionType)
        {
            case CouncilMissions.AttendCourt:
                ResolveAttendCourt(court, envoyName, courtName, lines);
                break;
            case CouncilMissions.PresentGifts:
                ResolvePresentGifts(court, mission, envoy, envoyName, courtName, lines);
                break;
            case CouncilMissions.GatherIntelligence:
                ResolveGatherIntelligence(cycle, court, envoy, envoyName, courtName, lines);
                break;
            case CouncilMissions.PetitionMinor:
                ResolvePetition(cycle, court, mission, envoyName, courtName, lines);
                break;
        }

        SaveManager.MarkDirty();
    }

    private static void ResolveAttendCourt(CourtState court, string envoyName,
                                           string courtName, List<string> lines)
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
            lines.Add($"{envoyName} attended {courtName}, but found no willing ear.");
            return;
        }

        target.Regard = Mathf.Clamp(target.Regard + 1, -3, 3);
        string opener = firstContact
            ? $"{envoyName} has been received at {courtName} for the first time."
            : $"{envoyName} attended {courtName}.";
        lines.Add($"{opener} {FirstName(target.DisplayName)} the {OfficeDisplay(target.Office)} " +
                  $"warms to the guild (Regard {Signed(target.Regard)}). " +
                  $"Standing: {court.Band()}.");
    }

    private static void ResolvePresentGifts(CourtState court, EnvoyMission mission,
        Companion envoy, string envoyName, string courtName, List<string> lines)
    {
        var target = court.GetCourtier(mission.TargetCourtierId);
        if (target == null)
        {
            lines.Add($"{envoyName}'s gift found no recipient at {courtName}.");
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
        lines.Add($"{envoyName} presented gifts to {FirstName(target.DisplayName)} " +
                  $"the {OfficeDisplay(target.Office)} at {courtName}: {verdict} " +
                  $"(Regard {Signed(target.Regard)}). Standing: {court.Band()}.");
    }

    private static void ResolveGatherIntelligence(CycleState cycle, CourtState court,
        Companion envoy, string envoyName, string courtName, List<string> lines)
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
        lines.Add($"{envoyName} worked the shadows of {courtName}: {intel}.{secret} " +
                  $"(Exposure {court.Exposure}/10.)");
    }

    private static void ResolvePetition(CycleState cycle, CourtState court,
        EnvoyMission mission, string envoyName, string courtName, List<string> lines)
    {
        court.HasContact = true;

        // Resolution-time backstop for the Welcome gate — covers standing
        // dropping mid-mission and any un-gated dispatch path.
        if (court.Band() < CourtStandingBand.Welcome)
        {
            lines.Add($"{envoyName}'s petition at {courtName} was heard politely and " +
                      $"declined — the guild's standing does not yet command favors.");
            return;
        }

        var target = court.GetCourtier(mission.TargetCourtierId);
        if (!CouncilLedger.IsReceptive(target) ||
            !CouncilLedger.IsPetitionableOffice(target.Office))
        {
            lines.Add($"{envoyName}'s petition at {courtName} found no willing patron.");
            return;
        }

        var favor = CouncilLedger.MintPetitionFavor(cycle, court, target,
            $"Petitioned of {target.DisplayName} the {OfficeDisplay(target.Office)} at {courtName}");
        lines.Add($"{envoyName} secured a favor at {courtName}: {favor.Type} (minor), " +
                  $"owed by {FirstName(target.DisplayName)} the {OfficeDisplay(target.Office)}.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

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
