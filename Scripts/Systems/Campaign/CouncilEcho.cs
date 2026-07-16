using Godot;
using System.Collections.Generic;

// ============================================================
// CouncilEcho.cs
//
// Purpose:        Word Spreads (Court & Council phase C4): the
//                 deed -> echo pipeline. Expedition outcomes in
//                 a kingdom's territory emit EchoEvents that
//                 land at the court on a later lunation tick and
//                 move courtier Regard, with full attribution in
//                 the Herald's Report.
//
//                 Rulings encoded (v1.2):
//                   - Routing miss DISSIPATES with a report line
//                     (courts have shapes in both directions).
//                   - Political call-in auto-cancels the worst
//                     in-flight negative echo (major first, then
//                     earliest landing).
//                   - Echo delay 1 lunation; 0 with a Courier
//                     Station (tier >= 1).
//                   - Negotiation deeds keyed off ReputationDelta
//                     sign (no star system exists).
// Layer:          System
// Collaborators:  CouncilState.cs (EchoEvent), CouncilTick.cs
//                 (calls LandEchoes as §13 step 1),
//                 ExpeditionManager.cs (deed emission sites +
//                 Political call-in), CouncilQueries (building
//                 tiers), court_council_system_v1_1.docx §7
// ============================================================

/// <summary>Deed emission, echo landing/routing, and echo cancellation
/// for the Word Spreads layer. Stateless; state lives in
/// CycleState.Council.EchoesInFlight.</summary>
public static class CouncilEcho
{
    // ── Deed tags ────────────────────────────────────────────────────────
    // Composite tags carry an argument after ':' (the NPC archetype for
    // negotiation deeds). Routing is derived from the tag at LANDING time.
    public const string PatrolSlain = "patrol_slain";
    public const string CorruptionCleansed = "corruption_cleansed";
    public const string SettlementDefended = "settlement_defended";
    public const string DealFair = "deal_fair";        // deal_fair:<Archetype>
    public const string DealExploit = "deal_exploit";  // deal_exploit:<Archetype>

    // S5 (overworld_spell_system §6a): the world watches magic the same
    // way it watches swords. Emitted from the spell-resolution step
    // (ExpeditionManager.SpellEmitWitnessEcho) and the Parley Compulsion
    // conversion; PatrolCompelled is the one echo that can be buried in
    // flight by the guild's OWN conduct (a Cordial resolution).
    public const string SpellcraftAid = "spellcraft_aid";
    public const string SpellcraftTransgression = "spellcraft_transgression";
    public const string PatrolCompelled = "patrol_compelled";

    // ═════════════════════════════════════════════════════════════════════
    // Emission (deed time, mid-expedition)
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>Emit one echo toward a kingdom's court. Returns the deed-time
    /// toast text, or null if no echo was emitted (no court — e.g. the
    /// convergence — or bad args). Delay is 1 lunation, 0 with a Courier
    /// Station.</summary>
    public static string EmitDeed(CycleState cycle, string kingdomId,
        string deedTag, bool positive, bool isMajor)
    {
        if (cycle?.Council == null || string.IsNullOrEmpty(kingdomId) ||
            !cycle.Council.Courts.ContainsKey(kingdomId))
        {
            return null;
        }

        int delay = CouncilQueries.BuildingTier(SaveManager.ActiveSave, "courier_station") >= 1
            ? 0 : 1;

        cycle.Council.EchoesInFlight.Add(new EchoEvent
        {
            KingdomId = kingdomId,
            DeedTag = deedTag,
            Valence = positive ? 1 : -1,
            IsMajor = isMajor,
            LandsOnLunation = cycle.Calendar.CurrentLunation + delay,
            Cancelled = false,
        });
        SaveManager.MarkDirty();

        string courtName = CouncilTick.CourtDisplayName(cycle, kingdomId);
        return positive
            ? $"Word of this will reach the court of {courtName}."
            : $"Word of this will reach the court of {courtName} — and it will not please them.";
    }

    // ═════════════════════════════════════════════════════════════════════
    // Landing (§13 step 1 — called from CouncilTick BEFORE obligation decay)
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>Land every echo whose LandsOnLunation has arrived: apply
    /// Regard to routed courtiers (up to 1 minor / 2 major), dissipate with
    /// a report line when no courtier matches, and report buried
    /// (cancelled) stories. Removes landed echoes from flight.</summary>
    public static void LandEchoes(CycleState cycle, List<HeraldReport> reports)
    {
        var council = cycle?.Council;
        if (council == null || council.EchoesInFlight.Count == 0)
        {
            return;
        }

        int now = cycle.Calendar.CurrentLunation;
        var landing = new List<EchoEvent>();
        foreach (var e in council.EchoesInFlight)
        {
            if (e.LandsOnLunation <= now)
            {
                landing.Add(e);
            }
        }
        if (landing.Count == 0)
        {
            return;
        }

        foreach (var echo in landing)
        {
            council.EchoesInFlight.Remove(echo);

            if (!council.Courts.TryGetValue(echo.KingdomId, out var court))
            {
                continue; // court vanished; the story dies with it
            }
            string courtName = CouncilTick.CourtDisplayName(cycle, echo.KingdomId);
            string deed = DeedDescription(echo.DeedTag);

            if (echo.Cancelled)
            {
                reports.Add(new HeraldReport
                {
                    Lunation = now,
                    KingdomId = echo.KingdomId,
                    Text = $"A story bound for {courtName} — {deed} — was quietly buried before it landed.",
                });
                continue;
            }

            var targets = RouteTargets(court, echo);
            if (targets.Count == 0)
            {
                reports.Add(new HeraldReport
                {
                    Lunation = now,
                    KingdomId = echo.KingdomId,
                    Text = $"A tale of {deed} reached {courtName}, but found no ears that cared.",
                });
                continue;
            }

            var names = new List<string>();
            foreach (var c in targets)
            {
                c.Regard = Mathf.Clamp(c.Regard + echo.Valence, -3, 3);
                names.Add($"{c.DisplayName} the {CouncilTick.OfficeDisplay(c.Office)} " +
                          $"(Regard {(c.Regard > 0 ? "+" : "")}{c.Regard})");
            }
            bool plural = targets.Count > 1;
            string verb = echo.Valence > 0
                ? (plural ? "approve" : "approves")
                : (plural ? "take offense" : "takes offense");
            string joined = string.Join("; ", names);
            reports.Add(new HeraldReport
            {
                Lunation = now,
                KingdomId = echo.KingdomId,
                Text = $"Word reaches {courtName} of {deed}: {joined} {verb}. " +
                       $"Standing: {court.Band()}.",
            });
        }
        SaveManager.MarkDirty();
    }

    /// <summary>Courtiers this echo lands on, per the §7a routing table.
    /// Up to 1 target for minor echoes, 2 for major; highest Influence
    /// first, Regard breaking ties (news reaches the powerful first).</summary>
    private static List<CourtierState> RouteTargets(CourtState court, EchoEvent echo)
    {
        string baseTag = echo.DeedTag;
        string arg = "";
        int colon = echo.DeedTag.IndexOf(':');
        if (colon >= 0)
        {
            baseTag = echo.DeedTag.Substring(0, colon);
            arg = echo.DeedTag.Substring(colon + 1);
        }

        var candidates = new List<CourtierState>();
        foreach (var c in court.Courtiers)
        {
            bool match = baseTag switch
            {
                PatrolSlain => c.Archetype == "Commander" || c.Office == "Chancellor",
                CorruptionCleansed => c.Archetype == "Commander" || c.Archetype == "Idealist",
                SettlementDefended => c.Archetype == "Commander" || c.Office == "Favorite",
                DealFair => c.Archetype == arg,
                DealExploit => c.Archetype == arg,
                // S5 (§6a): spellcraft lands on those who mind the arcane —
                // the Court Wizard's office and Idealist temperaments.
                SpellcraftAid => c.Office == CourtVocab.OfficeCourtWizard ||
                                 c.Archetype == "Idealist",
                SpellcraftTransgression => c.Office == CourtVocab.OfficeCourtWizard ||
                                           c.Archetype == "Idealist",
                // Compulsion of the kingdom's own soldiers is a matter of
                // state, not of magic: Chancellor and Commanders.
                PatrolCompelled => c.Office == CourtVocab.OfficeChancellor ||
                                   c.Archetype == "Commander",
                _ => false,
            };
            if (match)
            {
                candidates.Add(c);
            }
        }

        // Exploitative deals fall back to the Steward if no archetype match —
        // someone always minds the kingdom's purse (§7a).
        if (candidates.Count == 0 && baseTag == DealExploit)
        {
            foreach (var c in court.Courtiers)
            {
                if (c.Office == "Steward")
                {
                    candidates.Add(c);
                }
            }
        }

        candidates.Sort((a, b) =>
            a.Influence != b.Influence ? b.Influence - a.Influence : b.Regard - a.Regard);

        int take = echo.IsMajor ? 2 : 1;
        if (candidates.Count > take)
        {
            candidates.RemoveRange(take, candidates.Count - take);
        }
        return candidates;
    }

    private static string DeedDescription(string deedTag)
    {
        string baseTag = deedTag;
        int colon = deedTag.IndexOf(':');
        if (colon >= 0)
        {
            baseTag = deedTag.Substring(0, colon);
        }
        return baseTag switch
        {
            PatrolSlain => "the kingdom's own soldiers slain by the guild",
            CorruptionCleansed => "corrupted ground cleansed in the kingdom's territory",
            SettlementDefended => "a threat put down at a settlement's approaches",
            DealFair => "an honest bargain struck with one of the kingdom's own",
            DealExploit => "one of the kingdom's own fleeced at the table",
            SpellcraftAid => "great warding worked over the kingdom's people",
            SpellcraftTransgression => "necromancy worked openly in the kingdom's lands",
            PatrolCompelled => "the kingdom's own patrol bent by enchantment",
            _ => "the guild's doings",
        };
    }

    /// <summary>S5: cancel the most recent in-flight, uncancelled echo of a
    /// specific deed against a kingdom. The Parley Compulsion hook (§7f):
    /// a compulsion table that resolves Cordial buries its own story.
    /// Returns true when an echo was buried.</summary>
    public static bool CancelDeed(CouncilState council, string kingdomId, string deedTag)
    {
        if (council == null || string.IsNullOrEmpty(kingdomId))
        {
            return false;
        }
        EchoEvent target = null;
        foreach (var e in council.EchoesInFlight)
        {
            if (e.KingdomId == kingdomId && e.DeedTag == deedTag && !e.Cancelled)
            {
                target = e; // last match wins — the most recent telling
            }
        }
        if (target == null)
        {
            return false;
        }
        target.Cancelled = true;
        SaveManager.MarkDirty();
        return true;
    }

    // ═════════════════════════════════════════════════════════════════════
    // Political call-in (auto-cancel, ruled v1.2)
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>True if the kingdom has an in-flight, uncancelled negative
    /// echo — the Political call-in's eligibility condition.</summary>
    public static bool HasCancellableNegative(CouncilState council, string kingdomId)
    {
        if (council == null)
        {
            return false;
        }
        foreach (var e in council.EchoesInFlight)
        {
            if (e.KingdomId == kingdomId && e.Valence < 0 && !e.Cancelled)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Cancel the WORST in-flight negative echo against the given
    /// kingdom (major before minor, then earliest landing). Returns the
    /// buried deed's description, or null if none qualified.</summary>
    public static string CancelWorstNegative(CouncilState council, string kingdomId)
    {
        EchoEvent worst = null;
        foreach (var e in council.EchoesInFlight)
        {
            if (e.KingdomId != kingdomId || e.Valence >= 0 || e.Cancelled)
            {
                continue;
            }
            if (worst == null ||
                (e.IsMajor && !worst.IsMajor) ||
                (e.IsMajor == worst.IsMajor && e.LandsOnLunation < worst.LandsOnLunation))
            {
                worst = e;
            }
        }
        if (worst == null)
        {
            return null;
        }
        worst.Cancelled = true;
        SaveManager.MarkDirty();
        return DeedDescription(worst.DeedTag);
    }
}
