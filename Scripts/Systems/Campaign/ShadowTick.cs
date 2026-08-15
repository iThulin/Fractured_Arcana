using Godot;
using System.Collections.Generic;

// ============================================================
// ShadowTick.cs
//
// Purpose:        The espionage layer's per-lunation resolution
//                 (phase E2) plus the player-facing acquisition API.
//                 Two tick entry points, called from CouncilTick.Tick
//                 between mission resolution (step 4) and the exposure
//                 loop (step 5), per espionage_veiled_concord_spec_v1
//                 §5:
//                   ResolveYields()      — Watcher/Cutout passive
//                                          yields + Access ripen.
//                   ResolveCounterIntel()— the Spymaster/agent burn
//                                          rolls that hunt the network.
//                 A court-embedded burn spikes that court's Exposure
//                 and shields it from idle decay this tick, so the
//                 EXISTING CouncilTick exposure edge-check fires the
//                 Scandal — no parallel consequence system.
//
//                 SCOPE: the full tick. Watcher + Cutout + Saboteur
//                 yields; counter-intelligence burns; Concord contract
//                 completions (Plant/Intel/Theft/Sabotage/Extraction/
//                 Assassination) and against-guild resolution; Marked
//                 decay, court blackmail, and the Astrologer's contracts.
//                 Active player verbs (Saboteur strike, false echo,
//                 exfiltrate) live in ShadowOps; the marketplace in
//                 ShadowMarket. Assassination resolves automatically for
//                 now — see the interactive-broker SEAM in ShadowMarket.
// Layer:          System
// Collaborators:  CouncilTick.cs (calls both entry points; owns the
//                 exposure loop the burn spike feeds),
//                 CouncilState.cs / ShadowState.cs (state),
//                 CouncilQueries (campus building tiers),
//                 WorldData (tile Discovery charting)
// See:            espionage_veiled_concord_spec_v1.md §2c, §2e, §5, §12
// ============================================================

public static class ShadowTick
{
    // ── Yield tuning (§12 STARTING VALUES — tune here) ───────────────────
    /// <summary>Unseen tiles a Watcher charts per lunation, by Access (1..3).
    /// Index 0 unused so Access maps directly.</summary>
    private static readonly int[] ChartBudget = { 0, 6, 10, 14 };

    /// <summary>Concord Favor a contacted Cutout fences per lunation, by Access.</summary>
    private static readonly int[] CutoutFavor = { 0, 1, 2, 3 };

    /// <summary>Access at/above which a Cutout can dig a courtier's secret.</summary>
    private const int CutoutSecretAccess = 2;

    /// <summary>Per-lunation chance (%) a qualifying Cutout reveals one unknown
    /// secret in its court — ~2 lunations expected, the spec's "over 2 lunations".</summary>
    private const int CutoutSecretChance = 50;

    /// <summary>Lunations in place per +1 Access (Library halves — §6).</summary>
    private const int RipenLunations = 3;
    private const int RipenLunationsLibrary = 2;

    // ── Counter-intelligence tuning (§2e — the most sensitive weight) ────
    private const int BurnBasePerThreat = 10;   // hit% per threat point
    private const int BurnCoverWeight = 2;      // hit% shed per Cover point
    private const int BurnHandlerMitigation = 12;
    private const int BurnEmbassyCourtCover = 5; // court-embedded, Embassy built
    private const int BurnHitMin = 2;
    private const int BurnHitMax = 85;

    /// <summary>Exposure a COURT-EMBEDDED burn spikes onto its host court — the
    /// network is traced back and the guild's envoy pays for it (§2e).</summary>
    private const int CourtEmbeddedBurnSpike = 3;

    /// <summary>Extra hit% against a Saboteur — action is loud, so wreckers are
    /// caught soonest (§2c).</summary>
    private const int BurnSaboteurLoud = 10;

    // ── Step 4 (espionage): passive yields + ripen ───────────────────────

    /// <summary>Resolve every informant's passive yield and ripen Access. Emits
    /// Shadow Ledger lines into the tick's report list. No exposure mutation
    /// here — that is ResolveCounterIntel's job.</summary>
    public static void ResolveYields(CycleState cycle, List<HeraldReport> reports)
    {
        var council = cycle?.Council;
        if (council == null || council.Informants.Count == 0)
        {
            return;
        }
        int lun = cycle.Calendar.CurrentLunation;
        var save = SaveManager.ActiveSave;
        int ripen = (save != null &&
                     CouncilQueries.BuildingTier(save, ShadowVocab.BuildingArcaneLibrary) > 0)
            ? RipenLunationsLibrary : RipenLunations;
        bool changed = false;

        foreach (var inf in council.Informants)
        {
            // Ripen: +1 Access per `ripen` lunations survived, capped.
            int survived = lun - inf.LunationPlaced;
            int target = 1 + (survived / ripen);
            if (target > ShadowVocab.AccessMax) { target = ShadowVocab.AccessMax; }
            if (target > inf.Access)
            {
                inf.Access = target;
                changed = true;
                Emit(reports, lun, inf.KingdomId,
                    $"Shadow: your {Role(inf)} in {Court(cycle, inf.KingdomId)} works deeper in " +
                    $"(access {inf.Access}).");
            }

            int a = Mathf.Clamp(inf.Access, ShadowVocab.AccessMin, ShadowVocab.AccessMax);

            if (inf.Role == ShadowVocab.RoleWatcher)
            {
                int charted = ChartAround(cycle.World, inf.KingdomId, 1 + a, ChartBudget[a]);
                if (charted > 0)
                {
                    changed = true;
                    Emit(reports, lun, inf.KingdomId,
                        $"Shadow: your watcher charts {charted} tiles of {Court(cycle, inf.KingdomId)}.");
                }
            }
            else if (inf.Role == ShadowVocab.RoleCutout)
            {
                changed |= ResolveCutout(cycle, inf, a, lun, reports);
            }
            else if (inf.Role == ShadowVocab.RoleSaboteur)
            {
                // Passive erosion: undermine a siege pressing this kingdom a
                // little each lunation (the active strike is ShadowOps).
                var wf = FindDefendedWarfront(cycle, inf.KingdomId);
                if (wf != null && wf.Advance > 0)
                {
                    int before = wf.Advance;
                    wf.Advance = Mathf.Max(0, wf.Advance - ShadowVocab.SaboteurSiegePassive);
                    changed = true;
                    Emit(reports, lun, inf.KingdomId,
                        $"Shadow: your saboteur bleeds the siege pressing " +
                        $"{Court(cycle, inf.KingdomId)} ({before} → {wf.Advance}).");
                }
            }
        }

        if (changed)
        {
            SaveManager.MarkDirty();
        }
    }

    private static bool ResolveCutout(CycleState cycle, InformantState inf, int access,
                                      int lun, List<HeraldReport> reports)
    {
        var council = cycle.Council;
        bool changed = false;

        // Fence intel to the Concord for Favor — only once the guild has found
        // the cabal. Before contact, the cutout still gathers ground instead.
        if (council.ConcordContacted)
        {
            int favor = CutoutFavor[access];
            if (favor > 0)
            {
                council.ConcordFavor += favor;
                changed = true;
                Emit(reports, lun, inf.KingdomId,
                    $"Shadow: your cutout in {Court(cycle, inf.KingdomId)} fences intel " +
                    $"(+{favor} Concord favor, {council.ConcordFavor} banked).");
            }
        }
        else
        {
            int charted = ChartAround(cycle.World, inf.KingdomId, 1 + access, ChartBudget[access] / 2);
            if (charted > 0)
            {
                changed = true;
                Emit(reports, lun, inf.KingdomId,
                    $"Shadow: your cutout maps {charted} tiles of {Court(cycle, inf.KingdomId)}.");
            }
        }

        // Dig a secret (access-gated, chance-based — the "over 2 lunations" work).
        if (access >= CutoutSecretAccess &&
            council.Courts.TryGetValue(inf.KingdomId, out var court) &&
            (int)(GD.Randi() % 100) < CutoutSecretChance)
        {
            foreach (var c in court.Courtiers)
            {
                if (!c.SecretKnown)
                {
                    c.SecretKnown = true;
                    changed = true;
                    Emit(reports, lun, inf.KingdomId,
                        $"Shadow: your cutout uncovers a secret of {c.DisplayName} " +
                        $"the {CouncilTick.OfficeDisplay(c.Office)}.");
                    break;
                }
            }
        }

        return changed;
    }

    // ── Step 5 (espionage): counter-intelligence burns ───────────────────

    /// <summary>Roll counter-intelligence against every informant. On a burn the
    /// asset is removed; a court-embedded burn spikes its host court's Exposure
    /// and adds the court to <paramref name="intelShieldedCourts"/> so the
    /// following exposure loop does NOT decay the spike away this tick — letting
    /// the existing Scandal edge-check fire. Runs BEFORE that loop.</summary>
    public static void ResolveCounterIntel(CycleState cycle, List<HeraldReport> reports,
                                           HashSet<string> intelShieldedCourts)
    {
        var council = cycle?.Council;
        if (council == null || council.Informants.Count == 0)
        {
            return;
        }
        int lun = cycle.Calendar.CurrentLunation;
        var save = SaveManager.ActiveSave;
        int embassyTier = save != null ? CouncilQueries.EmbassyTier(save) : 0;
        int undercroft = save != null
            ? CouncilQueries.BuildingTier(save, ShadowVocab.BuildingUndercroft) : 0;
        // Marked >= Sold Out: the Concord fences the guild's movements — the
        // Astrologer's agents get one free extra burn roll per lunation (§3d).
        bool soldOut = council.Marked >= ShadowVocab.MarkedSoldOut;
        bool changed = false;

        // Iterate a copy — burns remove entries.
        foreach (var inf in new List<InformantState>(council.Informants))
        {
            council.Courts.TryGetValue(inf.KingdomId, out var court);
            int threat = Threat(court, inf);
            if (threat <= 0)
            {
                continue; // no counter-intelligence apparatus here — the network rests easy
            }

            int hit = BurnBasePerThreat * threat - BurnCoverWeight * inf.Cover;
            if (!string.IsNullOrEmpty(inf.HandlerCompanionId)) { hit -= BurnHandlerMitigation; }
            else if (undercroft >= ShadowVocab.UndercroftHandlerTier)
            {
                hit -= ShadowVocab.UndercroftHandlerMitigation; // §6: the Undercroft runs it
            }
            bool courtEmbedded = !string.IsNullOrEmpty(inf.CourtierId);
            if (courtEmbedded && embassyTier > 0) { hit -= BurnEmbassyCourtCover; }
            if (inf.Role == ShadowVocab.RoleSaboteur) { hit += BurnSaboteurLoud; }
            hit = Mathf.Clamp(hit, BurnHitMin, BurnHitMax);

            bool caught = (int)(GD.Randi() % 100) < hit;
            if (!caught && soldOut)
            {
                caught = (int)(GD.Randi() % 100) < hit; // the free extra roll
            }
            if (!caught)
            {
                continue; // survived this lunation
            }

            int damage = 1 + (threat >= 3 ? 1 : 0);
            inf.Cover -= damage;
            changed = true;

            if (inf.Cover > ShadowVocab.CoverMin)
            {
                Emit(reports, lun, inf.KingdomId,
                    $"Shadow: your {Role(inf)} in {Court(cycle, inf.KingdomId)} is nearly made " +
                    $"(cover {inf.Cover}).");
                continue;
            }

            // Burned. Remove the asset; a court-embedded burn is traced home.
            council.Informants.Remove(inf);

            if (courtEmbedded && court != null)
            {
                court.Exposure = Mathf.Clamp(court.Exposure + CourtEmbeddedBurnSpike, 0, 10);
                intelShieldedCourts.Add(inf.KingdomId);
                var catcher = SpymasterOf(court);
                string by = catcher != null
                    ? $"{catcher.DisplayName} the {CouncilTick.OfficeDisplay(catcher.Office)}"
                    : "the court";
                Emit(reports, lun, inf.KingdomId,
                    $"Shadow: your agent inside {Court(cycle, inf.KingdomId)} is caught — {by} " +
                    $"traces the network to the guild (Exposure +{CourtEmbeddedBurnSpike}).");
            }
            else
            {
                Emit(reports, lun, inf.KingdomId,
                    $"Shadow: your {Role(inf)} in {Court(cycle, inf.KingdomId)} is uncovered and lost.");
            }
        }

        if (changed)
        {
            SaveManager.MarkDirty();
        }
    }

    // ── Step 4c (espionage): Concord contract completions ────────────────

    /// <summary>Advance and complete guild-commissioned Concord contracts. On
    /// completion the effect applies and dealings + Marked are booked (so a
    /// commission that never lands costs Favor but leaves no mark). Returns true
    /// if any contract completed this tick — the dealing that shields Marked
    /// from idle decay. Astrologer contracts (AgainstPlayer) are E5.</summary>
    public static bool ResolveContracts(CycleState cycle, List<HeraldReport> reports)
    {
        var council = cycle?.Council;
        if (council == null || council.ConcordContracts.Count == 0)
        {
            return false;
        }
        int lun = cycle.Calendar.CurrentLunation;
        bool dealt = false;

        foreach (var c in new List<ConcordContract>(council.ConcordContracts))
        {
            c.LunationsRemaining -= 1;
            if (c.LunationsRemaining > 0)
            {
                continue;
            }
            council.ConcordContracts.Remove(c);
            if (c.AgainstPlayer)
            {
                // Not outbid in time — the knife lands (§3d threshold 9).
                ApplyAgainstPlayer(cycle, c, lun, reports);
                continue;
            }

            ApplyContractEffect(cycle, c, lun, reports);
            council.ConcordDealings += 1;

            // Undercroft III shaves Marked off each contract (§6).
            int undercroft = SaveManager.ActiveSave != null
                ? CouncilQueries.BuildingTier(SaveManager.ActiveSave, ShadowVocab.BuildingUndercroft) : 0;
            int markedGain = MarkedGainFor(c.ContractType);
            if (undercroft >= ShadowVocab.UndercroftMarkedDiscountTier)
            {
                markedGain = Mathf.Max(0, markedGain - ShadowVocab.UndercroftMarkedDiscount);
            }
            ShadowMarket.AddMarked(council, markedGain);
            dealt = true;
        }

        if (dealt)
        {
            SaveManager.MarkDirty();
        }
        return dealt;
    }

    private static void ApplyContractEffect(CycleState cycle, ConcordContract c, int lun,
                                            List<HeraldReport> reports)
    {
        string where = Court(cycle, c.TargetKingdomId);
        if (c.ContractType == ShadowVocab.ContractPlantAsset)
        {
            var inf = PlantInformant(cycle, c.TargetKingdomId, ShadowVocab.RoleCutout,
                ShadowVocab.CoverStartConcordBought, c.TargetId);
            Emit(reports, lun, c.TargetKingdomId, inf != null
                ? $"Shadow: the Concord embeds an agent in {where} (cover {inf.Cover})."
                : $"Shadow: the Concord's plant in {where} found no opening.");
        }
        else if (c.ContractType == ShadowVocab.ContractPurchaseIntel)
        {
            int revealed = RevealPois(cycle.World, c.TargetKingdomId, ShadowVocab.PurchaseIntelPoiReveal);
            Emit(reports, lun, c.TargetKingdomId,
                $"Shadow: the Concord sells the guild {revealed} site(s) in {where}.");
        }
        else if (c.ContractType == ShadowVocab.ContractTheft)
        {
            bool stole = StealSecret(cycle, c.TargetKingdomId, c.TargetId, lun, reports, where);
            if (!stole)
            {
                Emit(reports, lun, c.TargetKingdomId,
                    $"Shadow: the Concord's thief in {where} found no secret left to take.");
            }
        }
        else if (c.ContractType == ShadowVocab.ContractSabotage)
        {
            ApplySabotage(cycle, c.TargetKingdomId, c.TargetId, ShadowVocab.ConcordSiegeBreak,
                lun, reports, "the Concord");
        }
        else if (c.ContractType == ShadowVocab.ContractExtraction)
        {
            ExtractEnvoy(cycle, c.TargetKingdomId, c.TargetId, lun, reports);
        }
        else if (c.ContractType == ShadowVocab.ContractAssassination)
        {
            AssassinateCourtier(cycle, c.TargetKingdomId, c.TargetId, lun, reports);
        }
    }

    /// <summary>Remove a courtier from the world permanently (§3c Tier C). The
    /// office falls vacant, any Patron oath with them breaks, and the court
    /// investigates its dead (a heavy Exposure spike). Standing recomputes from
    /// the survivors — assassinating a blocker (negative Regard) opens the court;
    /// killing an ally throws it away. Irreversible.</summary>
    private static void AssassinateCourtier(CycleState cycle, string kingdomId,
                                            string courtierId, int lun, List<HeraldReport> reports)
    {
        if (!cycle.Council.Courts.TryGetValue(kingdomId, out var court))
        {
            Emit(reports, lun, kingdomId, "Shadow: the mark's court could not be found.");
            return;
        }
        var mark = court.GetCourtier(courtierId);
        if (mark == null)
        {
            Emit(reports, lun, kingdomId, "Shadow: the mark was already gone from the court.");
            return;
        }

        court.Courtiers.Remove(mark);
        if (court.PatronCourtierId == courtierId)
        {
            court.PatronCourtierId = "";
        }
        court.Exposure = Mathf.Clamp(court.Exposure + ShadowVocab.AssassinationExposureSpike, 0, 10);

        Emit(reports, lun, kingdomId,
            $"Shadow: {mark.DisplayName} the {CouncilTick.OfficeDisplay(mark.Office)} at " +
            $"{Court(cycle, kingdomId)} is dead — the office falls vacant and the court reels. " +
            $"Standing: {court.Band()}.");
    }

    /// <summary>Free an imprisoned envoy without the Prison-POI expedition (§3c).
    /// Mirrors ExpeditionManager.ReleaseImprisonedAt: remove the record (the
    /// companion returns to the pool via the derived party guard) and consume the
    /// now-empty gaol POI by its stable coordinates. Targets the named captive if
    /// given, else the first held in the kingdom, else the first held anywhere.</summary>
    private static void ExtractEnvoy(CycleState cycle, string kingdomId, string companionId,
                                     int lun, List<HeraldReport> reports)
    {
        var council = cycle.Council;
        ImprisonedEnvoy freed = null;
        if (!string.IsNullOrEmpty(companionId))
        {
            freed = council.Imprisoned.Find(e => e.CompanionId == companionId);
        }
        if (freed == null && !string.IsNullOrEmpty(kingdomId))
        {
            freed = council.Imprisoned.Find(e => e.KingdomId == kingdomId);
        }
        if (freed == null && council.Imprisoned.Count > 0)
        {
            freed = council.Imprisoned[0];
        }
        if (freed == null)
        {
            Emit(reports, lun, kingdomId,
                "Shadow: the Concord's extraction team found no cell to crack — none are held.");
            return;
        }

        council.Imprisoned.Remove(freed);

        // Consume the now-empty gaol so it stops offering a rescue.
        var world = cycle.World;
        if (world != null)
        {
            var poi = world.PoiAt(freed.PrisonX, freed.PrisonY);
            if (poi != null && poi.Kind == PoiKind.Prison)
            {
                poi.Consumed = true;
            }
        }

        var envoy = cycle.Companions.Find(c => c.Id == freed.CompanionId);
        string name = envoy?.Name ?? freed.CompanionId;
        Emit(reports, lun, freed.KingdomId,
            $"Shadow: the Concord cracks the gaol at {Court(cycle, freed.KingdomId)} — " +
            $"{name} is spirited back to the guild's ranks.");
    }

    /// <summary>Resolve an Astrologer-commissioned contract against the guild that
    /// was not outbid in time (§3d threshold 9). "seize:&lt;id&gt;" gaols a
    /// companion (arc-scar for the cycle, ruling #5 — the same recoverable
    /// Imprisonment machinery); "burn" bleeds Cover across the whole network.</summary>
    private static void ApplyAgainstPlayer(CycleState cycle, ConcordContract c, int lun,
                                           List<HeraldReport> reports)
    {
        var council = cycle.Council;
        string flavor = c.TargetId ?? "";

        if (flavor.StartsWith(ShadowVocab.AgainstSeize))
        {
            int colon = flavor.IndexOf(':');
            string companionId = colon >= 0 ? flavor.Substring(colon + 1) : "";
            bool seized = CouncilTick.SeizeEnvoyToGaol(cycle, c.TargetKingdomId, companionId, lun);
            var envoy = cycle.Companions.Find(cc => cc.Id == companionId);
            string name = envoy?.Name ?? companionId;
            Emit(reports, lun, c.TargetKingdomId, seized
                ? $"Shadow: the shadows take their price — {name} is seized and gaoled at " +
                  $"{Court(cycle, c.TargetKingdomId)}. Storm it, or extract them, to bring them home."
                : $"Shadow: a killer came for {name}, but they slipped the noose — this time.");
            return;
        }

        // Mass burn: the network bleeds Cover; any asset run to ground is lost.
        int burned = 0, bled = 0;
        foreach (var inf in new List<InformantState>(council.Informants))
        {
            inf.Cover -= ShadowVocab.MassBurnCover;
            bled++;
            if (inf.Cover <= ShadowVocab.CoverMin)
            {
                council.Informants.Remove(inf);
                burned++;
            }
        }
        Emit(reports, lun, "", bled == 0
            ? "Shadow: the Astrologer's sweep found no network left to burn."
            : $"Shadow: the Astrologer's coin sweeps the guild's network — {bled} asset(s) bled, " +
              $"{burned} burned out.");
        SaveManager.MarkDirty();
    }

    private static int MarkedGainFor(string contractType)
    {
        if (contractType == ShadowVocab.ContractAssassination) { return ShadowVocab.MarkedGainAssassination; }
        if (contractType == ShadowVocab.ContractExtraction) { return ShadowVocab.MarkedGainExtraction; }
        if (contractType == ShadowVocab.ContractTheft) { return ShadowVocab.MarkedGainTheft; }
        if (contractType == ShadowVocab.ContractSabotage) { return ShadowVocab.MarkedGainSabotage; }
        if (contractType == ShadowVocab.ContractPurchaseIntel) { return ShadowVocab.MarkedGainPurchaseIntel; }
        return ShadowVocab.MarkedGainPlantAsset;
    }

    // ── Sabotage effects (shared by contracts, Saboteur strikes, §4) ─────

    /// <summary>Apply one sabotage effect and emit its Herald line. Siege variant
    /// pushes a defended warfront's Advance back toward repel; corruption variant
    /// requests a §4-capped one-lunation delay. <paramref name="actor"/> is the
    /// in-voice subject ("the Concord", "your saboteur").</summary>
    public static void ApplySabotage(CycleState cycle, string kingdomId, string variant,
        int siegeAmount, int lun, List<HeraldReport> reports, string actor)
    {
        string where = Court(cycle, kingdomId);
        if (variant == ShadowVocab.SabotageCorruption)
        {
            bool ok = CorruptionSpread.TryRequestDelay(kingdomId, cycle.Kingdoms);
            Emit(reports, lun, kingdomId, ok
                ? $"Shadow: {actor} stalls the corruption creeping into {where} — a lunation bought."
                : $"Shadow: {actor} moved against the corruption in {where}, but the shadows were " +
                  $"already stretched thin (no effect this lunation).");
        }
        else // siege
        {
            var wf = FindDefendedWarfront(cycle, kingdomId);
            if (wf == null)
            {
                Emit(reports, lun, kingdomId,
                    $"Shadow: {actor} found no siege pressing {where} to break.");
                return;
            }
            int before = wf.Advance;
            wf.Advance = Mathf.Max(0, wf.Advance - siegeAmount);
            Emit(reports, lun, kingdomId,
                $"Shadow: {actor} undermines the siege pressing {where} — the advance falls " +
                $"{before} → {wf.Advance}.");
        }
        SaveManager.MarkDirty();
    }

    /// <summary>An open, non-cache warfront where the kingdom is the defender —
    /// the front the guild's shadow-work would relieve.</summary>
    public static Warfront FindDefendedWarfront(CycleState cycle, string kingdomId)
    {
        if (cycle?.Warfronts == null)
        {
            return null;
        }
        foreach (var w in cycle.Warfronts)
        {
            if (!w.Closed && !w.IsCacheSiege && w.DefenderKingdomId == kingdomId)
            {
                return w;
            }
        }
        return null;
    }

    private static int RevealPois(WorldData world, string kingdomId, int max)
    {
        if (world == null || max <= 0)
        {
            return 0;
        }
        int revealed = 0;
        foreach (var poi in world.Pois)
        {
            if (revealed >= max) { break; }
            if (poi.KingdomId != kingdomId || poi.Discovered) { continue; }
            poi.Discovered = true;
            revealed++;
            ChartAround(world, kingdomId, 3, 24); // light chart around the newly-known ground
        }
        return revealed;
    }

    private static bool StealSecret(CycleState cycle, string kingdomId, string courtierId,
                                    int lun, List<HeraldReport> reports, string where)
    {
        if (!cycle.Council.Courts.TryGetValue(kingdomId, out var court))
        {
            return false;
        }
        var target = !string.IsNullOrEmpty(courtierId) ? court.GetCourtier(courtierId) : null;
        if (target != null && !target.SecretKnown)
        {
            target.SecretKnown = true;
            Emit(reports, lun, kingdomId,
                $"Shadow: the Concord steals a secret of {target.DisplayName} " +
                $"the {CouncilTick.OfficeDisplay(target.Office)} in {where}.");
            return true;
        }
        foreach (var cr in court.Courtiers)
        {
            if (!cr.SecretKnown)
            {
                cr.SecretKnown = true;
                Emit(reports, lun, kingdomId,
                    $"Shadow: the Concord steals a secret of {cr.DisplayName} " +
                    $"the {CouncilTick.OfficeDisplay(cr.Office)} in {where}.");
                return true;
            }
        }
        return false;
    }

    // ── Step 6b (espionage): Marked decay + thresholds ───────────────────

    /// <summary>Decay Marked toward 0 in lunations with no Concord dealing (idle
    /// or active-contract shields it), and surface a status line once it is high
    /// enough to matter. Passive consequences (the Sold-Out extra burn roll) are
    /// read live in ResolveCounterIntel; threshold 9 (Contracted Against) is
    /// E5.</summary>
    public static void ResolveMarked(CycleState cycle, List<HeraldReport> reports, bool dealtThisTick)
    {
        var council = cycle?.Council;
        if (council == null)
        {
            return;
        }

        int lun = cycle.Calendar.CurrentLunation;

        bool idle = !dealtThisTick && council.ConcordContracts.Count == 0;
        if (council.Marked > 0 && idle)
        {
            council.Marked -= 1;
            SaveManager.MarkDirty();
        }

        // Threshold 3 (Noticed): a courtier learns of the guild's dealings and
        // holds it — now the guild is the one with a secret. One-shot per cycle
        // (WorldFlag), a lasting standing mark at the court that caught it.
        if (council.Marked >= ShadowVocab.MarkedNoticed && !cycle.HasFlag(BlackmailFlag))
        {
            TryFireBlackmail(cycle, lun, reports);
        }

        // Threshold 9 (Contracted Against): the Astrologer buys the Concord
        // against the guild, unless one is already in play (§3d / §3e).
        if (council.Marked >= ShadowVocab.MarkedContracted && !HasAgainstContract(council)
            && (int)(GD.Randi() % 100) < ShadowVocab.AstrologerContractChance)
        {
            CommissionAgainstGuild(cycle, lun, reports);
        }

        if (council.Marked >= ShadowVocab.MarkedSoldOut)
        {
            Emit(reports, lun, "",
                $"Shadow: the Concord is fencing the guild's movements (Marked {council.Marked}/10).");
        }
        else if (council.Marked >= ShadowVocab.MarkedNoticed)
        {
            Emit(reports, lun, "",
                $"Shadow: the guild's dealings are being noticed (Marked {council.Marked}/10).");
        }
    }

    private const string BlackmailFlag = "shadow_blackmail_fired";

    private static bool HasAgainstContract(CouncilState council)
    {
        foreach (var c in council.ConcordContracts)
        {
            if (c.AgainstPlayer) { return true; }
        }
        return false;
    }

    /// <summary>A Favorite or Spymaster who learns of the guild's shadow dealings
    /// leverages them: a lasting StandingPenalty at the strongest such court.</summary>
    private static void TryFireBlackmail(CycleState cycle, int lun, List<HeraldReport> reports)
    {
        var council = cycle.Council;
        CourtState best = null;
        CourtierState holder = null;
        foreach (var court in council.Courts.Values)
        {
            foreach (var c in court.Courtiers)
            {
                if (c.Office != CourtVocab.OfficeFavorite && c.Office != CourtVocab.OfficeSpymaster)
                {
                    continue;
                }
                if (best == null || court.StandingScore() > best.StandingScore())
                {
                    best = court;
                    holder = c;
                }
            }
        }
        if (best == null)
        {
            return; // no one positioned to hold it — try again a later tick
        }

        best.StandingPenalty += ShadowVocab.BlackmailStandingPenalty;
        cycle.SetFlag(BlackmailFlag);
        SaveManager.MarkDirty();
        Emit(reports, lun, best.KingdomId,
            $"Shadow: {holder.DisplayName} the {CouncilTick.OfficeDisplay(holder.Office)} at " +
            $"{Court(cycle, best.KingdomId)} has learned of the guild's dealings with the Concord, " +
            $"and holds it over them (standing suffers). Standing: {best.Band()}.");
    }

    /// <summary>The Astrologer commissions against the guild: seize an envoy on
    /// mission (recoverable, ruling #5) if one is out, else a network sweep.</summary>
    private static void CommissionAgainstGuild(CycleState cycle, int lun, List<HeraldReport> reports)
    {
        var council = cycle.Council;
        string targetKingdom = "";
        string flavor = ShadowVocab.AgainstBurn;

        foreach (var m in council.ActiveMissions)
        {
            if (!m.Recalled && !string.IsNullOrEmpty(m.CompanionId))
            {
                targetKingdom = m.KingdomId;
                flavor = $"{ShadowVocab.AgainstSeize}:{m.CompanionId}";
                break;
            }
        }

        council.ConcordContracts.Add(new ConcordContract
        {
            Id = $"against_{lun}_{council.ConcordContracts.Count}",
            ContractType = ShadowVocab.ContractAssassination, // the cabal's lethal work
            TargetKingdomId = targetKingdom,
            TargetId = flavor,
            LunationsRemaining = ShadowVocab.AstrologerContractDuration,
            FavorPaid = ShadowVocab.AstrologerBidFavor,
            AgainstPlayer = true,
        });
        SaveManager.MarkDirty();

        bool seize = flavor.StartsWith(ShadowVocab.AgainstSeize);
        Emit(reports, lun, targetKingdom,
            seize
                ? $"Shadow: the Astrologer buys the Concord against the guild — a killer moves on an " +
                  $"envoy. Outbid them ({ShadowVocab.AstrologerBidFavor}+ favor) before the moon turns."
                : $"Shadow: the Astrologer buys the Concord against the guild's network. Outbid them " +
                  $"({ShadowVocab.AstrologerBidFavor}+ favor) before the moon turns.");
    }

    // ── Acquisition API (player actions; E2 debug + later UI) ─────────────

    /// <summary>Plant a standing informant. Returns the new asset, or null if the
    /// placement is invalid (unknown kingdom, or a court-embedded slot already
    /// filled on that courtier — one embed per courtier is the natural cap).</summary>
    public static InformantState PlantInformant(CycleState cycle, string kingdomId,
        string role, int coverStart, string courtierId = "", string warfrontId = "")
    {
        var council = cycle?.Council;
        if (council == null || string.IsNullOrEmpty(kingdomId))
        {
            return null;
        }

        // Undercroft concurrency cap (§6): the network cannot exceed what the
        // spine can run. 0 tier = a minimal network is still possible.
        var save = SaveManager.ActiveSave;
        int undercroft = save != null
            ? CouncilQueries.BuildingTier(save, ShadowVocab.BuildingUndercroft) : 0;
        if (council.Informants.Count >= ShadowVocab.InformantCap(undercroft))
        {
            return null; // at capacity — exfiltrate or build the Undercroft
        }

        if (!string.IsNullOrEmpty(courtierId))
        {
            foreach (var existing in council.Informants)
            {
                if (existing.CourtierId == courtierId)
                {
                    return null; // already embedded on this courtier
                }
            }
        }

        // Hall of Records renown (§6): a network re-placed in a kingdom the guild
        // has worked before starts with more Cover, up to a cap. Gated on the
        // records building; the banked value persists cross-cycle in the ledger.
        int cover = Mathf.Clamp(coverStart, ShadowVocab.CoverMin, ShadowVocab.CoverMax);
        if (save != null && CouncilQueries.BuildingTier(save, ShadowVocab.BuildingScriptorum) > 0)
        {
            int renown = Mathf.Min(BankedRenown(save, kingdomId), ShadowVocab.ExfilRenownCoverCap);
            cover = Mathf.Clamp(cover + renown, ShadowVocab.CoverMin, ShadowVocab.CoverMax);
        }

        var inf = new InformantState
        {
            Id = $"informant_{cycle.Calendar.CurrentLunation}_{council.Informants.Count}",
            KingdomId = kingdomId,
            CourtierId = courtierId ?? "",
            WarfrontId = warfrontId ?? "",
            Role = role,
            Cover = cover,
            Access = ShadowVocab.AccessMin,
            LunationPlaced = cycle.Calendar.CurrentLunation,
        };
        council.Informants.Add(inf);
        SaveManager.MarkDirty();
        return inf;
    }

    // ── Hall of Records exfiltration renown (§6) ─────────────────────────
    // Banked in EternalLedger.DeedCounts under a namespaced key (the same
    // cross-cycle store MarginaliaService uses), so no new save field. Value =
    // the highest Access ever exfiltrated from that kingdom.

    private static string RenownKey(string kingdomId) => $"shadow_cover_{kingdomId}";

    public static int BankedRenown(GuildSaveData save, string kingdomId)
    {
        if (save?.Ledger?.DeedCounts == null) { return 0; }
        return save.Ledger.DeedCounts.TryGetValue(RenownKey(kingdomId), out int n) ? n : 0;
    }

    /// <summary>Record an exfiltrated asset's Access as renown for its kingdom
    /// (max, not sum — the best network you ever ran there).</summary>
    public static void BankRenown(GuildSaveData save, string kingdomId, int access)
    {
        if (save?.Ledger?.DeedCounts == null || string.IsNullOrEmpty(kingdomId))
        {
            return;
        }
        string key = RenownKey(kingdomId);
        int cur = save.Ledger.DeedCounts.TryGetValue(key, out int n) ? n : 0;
        if (access > cur)
        {
            save.Ledger.DeedCounts[key] = access;
        }
    }

    /// <summary>Turn a known courtier's secret into a court-embedded informant
    /// (§2a). Requires the secret already discovered; caps at one embed per
    /// courtier. Returns the new asset or null on failure.</summary>
    public static InformantState TryTurnSecret(CycleState cycle, string kingdomId,
        string courtierId, string role = null)
    {
        var council = cycle?.Council;
        if (council == null || !council.Courts.TryGetValue(kingdomId, out var court))
        {
            return null;
        }
        var courtier = court.GetCourtier(courtierId);
        if (courtier == null || !courtier.SecretKnown)
        {
            return null; // no leverage to turn
        }
        return PlantInformant(cycle, kingdomId, role ?? ShadowVocab.RoleCutout,
            ShadowVocab.CoverStartTurned, courtierId);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>Counter-intelligence threat at a court: the Spymaster's Influence
    /// (0 if none), +1 if the court is already hot (Exposure >= Scandal line),
    /// +1 if the Astrologer's agent is present. Kingdoms with no court and no
    /// Spymaster have no hunter.</summary>
    private static int Threat(CourtState court, InformantState inf)
    {
        if (court == null)
        {
            return 0;
        }
        int threat = 0;
        var spy = SpymasterOf(court);
        if (spy != null) { threat += spy.Influence; }
        if (court.Exposure >= 4) { threat += 1; }
        foreach (var c in court.Courtiers)
        {
            if (c.IsCorruptedAgent) { threat += 1; break; }
        }
        return threat;
    }

    private static CourtierState SpymasterOf(CourtState court)
    {
        foreach (var c in court.Courtiers)
        {
            if (c.Office == CourtVocab.OfficeSpymaster)
            {
                return c;
            }
        }
        return null;
    }

    /// <summary>Flip up to <paramref name="budget"/> Unseen tiles within
    /// <paramref name="radius"/> of the kingdom's seat to Charted (never
    /// downgrades Explored). Falls back to any Unseen owned tile if the kingdom
    /// has no seat POI. Returns the count charted.</summary>
    private static int ChartAround(WorldData world, string kingdomId, int radius, int budget)
    {
        if (world == null || budget <= 0)
        {
            return 0;
        }

        // Anchor on the kingdom's seat if it has one.
        int ax = -1, ay = -1;
        foreach (var poi in world.Pois)
        {
            if (poi.Kind == PoiKind.Seat && poi.KingdomId == kingdomId)
            {
                ax = poi.X; ay = poi.Y;
                break;
            }
        }

        int charted = 0;
        if (ax >= 0)
        {
            for (int dy = -radius; dy <= radius && charted < budget; dy++)
            {
                for (int dx = -radius; dx <= radius && charted < budget; dx++)
                {
                    int x = ax + dx, y = ay + dy;
                    if (!world.InBounds(x, y)) { continue; }
                    if (HexCoord.OffsetDistance(ax, ay, x, y) > radius) { continue; }
                    charted += TryChart(world, x, y, kingdomId);
                }
            }
        }

        // Fallback / top-up: any Unseen owned tile, row-major.
        for (int i = 0; charted < budget && i < world.Tiles.Length; i++)
        {
            int x = i % world.Width, y = i / world.Width;
            charted += TryChart(world, x, y, kingdomId);
        }
        return charted;
    }

    private static int TryChart(WorldData world, int x, int y, string kingdomId)
    {
        int idx = y * world.Width + x;
        if (world.Tiles[idx].KingdomId != kingdomId) { return 0; }
        if (world.Tiles[idx].Discovery != TileDiscovery.Unseen) { return 0; }
        world.Tiles[idx].Discovery = TileDiscovery.Charted;
        return 1;
    }

    private static string Role(InformantState inf)
        => string.IsNullOrEmpty(inf.Role) ? "informant" : inf.Role.ToLower();

    private static string Court(CycleState cycle, string kingdomId)
        => CouncilTick.CourtDisplayName(cycle, kingdomId);

    private static void Emit(List<HeraldReport> reports, int lunation, string kingdomId,
                             string text)
    {
        reports.Add(new HeraldReport
        {
            Lunation = lunation,
            KingdomId = kingdomId ?? "",
            Text = text,
        });
    }
}
