using Godot;
using System.Collections.Generic;
using System.Text.Json;

// ============================================================
// CompanionInjurySystem.cs  (K2, 2026-07-09)
//
// Purpose:        §5b injury/death rolls and infirmary recovery —
//                 the demand-math's third leg. One roll per
//                 affected companion per wipe: Tier 1 injured;
//                 Tier 2 injured + 15% death; Tier 3 +30%; boss
//                 contexts 40%. Sworn subtract 10 points of death
//                 chance (earned plot armor). Injured = out of all
//                 three demands for 1–2 lunations, recovering at
//                 the campus (R24: infirmary rides Training Grounds
//                 tiers, interim — the MECHANIC is the commitment,
//                 the host building is not).
//
// SCOPE RULINGS (logged):
// - "Downed in a lost combat" = the whole fielded party: defeat
//   requires allPlayersDead (CheckCombatEnd), so downed == fielded.
//   Per-companion downed telemetry becomes necessary only if a
//   retreat mechanic ships.
// - Death sets IsPermadead + prints. The v1 morale RIPPLE and
//   signature destruction are K4 scope (they live in the loyalty
//   delta table K4 builds) — logged here so K4 picks them up.
// - One roll per wipe (§5b: "no injury state precedes death within
//   a single wipe — the roll is one roll").
//
// Layer:          System (campaign)
// Collaborators:  Companion (InjuredLunationsRemaining, tiers),
//                 ExpeditionManager (wipe call sites, territory
//                 tier), StrategicView (lunation tick → recovery),
//                 SaveManager (JsonOptions for the round-trip
//                 assertion — house rule for save-adjacent fields).
// See:            companion_item_systems_v2_1 §5b · docs/k2_verification.md
// ============================================================

public static class CompanionInjurySystem
{
    // §5b death chances by territory tier (starting values, R1: tuning targets).
    public const int Tier2DeathChance = 15;
    public const int Tier3DeathChance = 30;
    public const int BossDeathChance = 40;
    public const int SwornDeathReduction = 10;

    private static bool _roundTripAsserted = false;

    /// <summary>One §5b roll for every companion in the active party. Call on:
    /// lost combat (whole fielded party is down by definition), or expedition
    /// wipe (pool hit 0). NOT on won combats — heroism stays free.
    /// Returns a player-facing casualty summary ("" when nobody was affected)
    /// so the failure banner can say WHO was hurt, not just that the run died.</summary>
    public static string ApplyWipe(GuildSaveData save, int territoryTier, bool bossContext, string context)
    {
        if (save == null)
            return "";
        AssertRoundTripOnce();
        var summary = new System.Text.StringBuilder();

        int baseChance = bossContext ? BossDeathChance
            : territoryTier >= 3 ? Tier3DeathChance
            : territoryTier == 2 ? Tier2DeathChance
            : 0;

        GD.Print($"[Injury] Wipe rolls — {context} (territory tier {territoryTier}" +
                 $"{(bossContext ? ", BOSS" : "")}, base death {baseChance}%).");

        // K4 ordering: deltas and ripples apply AFTER every roll is made —
        // a mid-loop ripple could push a not-yet-rolled Sworn companion
        // below the threshold and strip their death-chance armor. Everyone
        // rolls against the loyalty they walked in with.
        var died = new List<Companion>();
        var survived = new List<Companion>();

        foreach (var id in save.ActivePartyCompanionIds)
        {
            var c = save.Companions.Find(x => x.Id == id && x.IsRecruited && !x.IsPermadead);
            if (c == null || c.IsInjured)
                continue;   // already recovering — not fielded, not re-rolled

            int chance = baseChance;
            if (c.GetLoyaltyTier() == LoyaltyTier.Sworn)
                chance = Mathf.Max(0, chance - SwornDeathReduction);

            int roll = (int)(GD.Randi() % 100);
            if (roll < chance)
            {
                c.IsPermadead = true;
                GD.Print($"[Injury] {c.Name} DIES (rolled {roll} < {chance}%" +
                         $"{(c.GetLoyaltyTier() == LoyaltyTier.Sworn ? ", Sworn −10 applied" : "")}).");
                summary.Append($" {c.Name} is DEAD.");

                // Death→memorial (§8): the fallen companion's signature technique
                // outlives them, entering the permanent card pool — the one reward
                // that makes loss accrue. Fires on permadeath at ANY arc stage, so it
                // is distinct from the arc-capstone grant (ProgressionSweep), which
                // needs the arc COMPLETED. Discover no-ops if the card is already
                // known (e.g. a completed capstone already granted it), and skips
                // Legendaries/Marginalia. Signature = the first contributed card.
                string memId = (c.ContributedCardIds != null && c.ContributedCardIds.Count > 0)
                    ? c.ContributedCardIds[0] : "";
                string memCard = CardAcquisition.Discover(save, memId);
                if (!string.IsNullOrEmpty(memCard))
                    summary.Append($" Their {memCard} passes into the guild's keeping.");

                died.Add(c);
            }
            else
            {
                // Severity roll: 1–2 lunations out of all three demands.
                c.InjuredLunationsRemaining = 1 + (int)(GD.Randi() % 2);
                GD.Print($"[Injury] {c.Name} injured — {c.InjuredLunationsRemaining} lunation(s) " +
                         $"in the infirmary (death roll {roll} ≥ {chance}%).");
                summary.Append($" {c.Name} injured — {c.InjuredLunationsRemaining} lunation(s).");
                survived.Add(c);
            }
        }

        // K4: surviving the wipe still costs — the run died and they carried
        // it home. Then the v1 morale ripple lands at last: each death moves
        // the whole living roster (Sworn dampened). Signature destruction is
        // automatic — signatures are derived (StanceRegistry.EligibleSignature)
        // and the dead never spawn.
        foreach (var c in survived)
            LoyaltyEvents.OnWipeSurvived(c);
        foreach (var c in died)
            LoyaltyEvents.OnDeathRipple(save, c);

        return summary.ToString().Trim();
    }

    /// <summary>Lunation-tick recovery (R24: Training Grounds interim host).
    /// Call once per new lunation, after CouncilTick.</summary>
    public static void TickRecovery(GuildSaveData save)
    {
        if (save == null)
            return;
        AssertRoundTripOnce();

        foreach (var c in save.Companions)
        {
            if (c == null || !c.IsInjured || c.IsPermadead)
                continue;
            c.InjuredLunationsRemaining--;
            GD.Print(c.IsInjured
                ? $"[Infirmary] {c.Name} recovering — {c.InjuredLunationsRemaining} lunation(s) left."
                : $"[Infirmary] {c.Name} has recovered and returns to the roster.");
        }
    }

    // ── K2.5 (ruled 2026-07-09): expedition HP persistence ───────────────

    /// <summary>Extraction threshold: below this % of BaseHP at expedition end
    /// → infirmary time. Tuning target.</summary>
    public const int ExtractionInjuryThresholdPct = 25;

    /// <summary>K2.5: expedition over (extraction) — check who came home
    /// broken. Stabilized at 0 (downed in a won fight) → 1–2 lunations;
    /// below 25% of BaseHP → 1 lunation. NO death risk — death stays a
    /// losing-fight thing (§5b). Resets ExpeditionHP for everyone.
    /// Returns a player-facing summary ("" when everyone came home whole).</summary>
    public static string ApplyExtractionCheck(GuildSaveData save)
    {
        if (save == null)
            return "";
        AssertRoundTripOnce();
        var summary = new System.Text.StringBuilder();

        foreach (var id in save.ActivePartyCompanionIds)
        {
            var c = save.Companions.Find(x => x.Id == id && x.IsRecruited && !x.IsPermadead);
            if (c == null || c.ExpeditionHP < 0)
                continue;

            int hp = c.ExpeditionHP;
            c.ExpeditionHP = -1;

            if (c.IsInjured)
                continue;   // already in the infirmary from an earlier wipe

            if (hp == 0)
            {
                c.InjuredLunationsRemaining = 1 + (int)(GD.Randi() % 2);
                GD.Print($"[Injury] {c.Name} was carried home — " +
                         $"{c.InjuredLunationsRemaining} lunation(s) in the infirmary.");
                summary.Append($" {c.Name} carried home — {c.InjuredLunationsRemaining} lunation(s).");
            }
            else if (hp * 100 < c.BaseHP * ExtractionInjuryThresholdPct)
            {
                c.InjuredLunationsRemaining = 1;
                GD.Print($"[Injury] {c.Name} extracted at {hp}/{c.BaseHP} HP — " +
                         "1 lunation in the infirmary.");
                summary.Append($" {c.Name} injured — 1 lunation.");
            }
        }
        return summary.ToString().Trim();
    }

    /// <summary>Overworld rest (2026-07-29): mend carried combat HP for every
    /// companion currently carrying, by <paramref name="fraction"/> of BaseHP
    /// (minimum 1), clamped to BaseHP. Skips stabilized-at-0 companions —
    /// being downed keeps you out of the fights for the rest of the run; a
    /// campfire does not undo that. Pass 1.0 for a full mend (outposts).</summary>
    public static void HealExpeditionHP(GuildSaveData save, float fraction)
    {
        if (save?.Companions == null)
            return;
        foreach (var c in save.Companions)
        {
            if (c == null || c.ExpeditionHP <= 0)
                continue;
            int heal = Mathf.Max(1, (int)(c.BaseHP * fraction));
            c.ExpeditionHP = Mathf.Min(c.BaseHP, c.ExpeditionHP + heal);
        }
    }

    /// <summary>K2.5: clear per-expedition HP — fresh launch, or after the
    /// expedition-end accounting (FailExpedition's wipe rolls already cover
    /// the injuries on that path).</summary>
    public static void ResetExpeditionHP(GuildSaveData save)
    {
        if (save?.Companions == null)
            return;
        foreach (var c in save.Companions)
            if (c != null)
                c.ExpeditionHP = -1;
    }

    /// <summary>House rule: round-trip assertion for save-adjacent fields, run
    /// once per session with the REAL save serializer options.</summary>
    private static void AssertRoundTripOnce()
    {
        if (_roundTripAsserted)
            return;
        _roundTripAsserted = true;

        var probe = new Companion { Id = "probe", InjuredLunationsRemaining = 2, ExpeditionHP = 7 };
        var back = JsonSerializer.Deserialize<Companion>(
            JsonSerializer.Serialize(probe, SaveManager.JsonOptions), SaveManager.JsonOptions);
        if (back == null || back.InjuredLunationsRemaining != 2 || back.ExpeditionHP != 7)
            GD.PrintErr("[K2 RoundTrip] Injury/ExpeditionHP FAILED to round-trip " +
                        "through SaveManager.JsonOptions — state will not persist!");
        else
            GD.Print("[K2 RoundTrip] InjuredLunationsRemaining + ExpeditionHP round-trip.");
    }
}
