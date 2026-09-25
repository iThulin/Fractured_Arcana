using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// FieldPostings.cs: what a field party can be LEFT to do (2026-09-24)
//
// Purpose:        Field Party v1, docs/field_party_task_force_spec_v1. A
//                 posting is FieldWork given teeth: a kind the tile allows,
//                 a Supplies cost paid at the new moon, an effect wired to a
//                 system that already ships (cache overseers, cache sieges,
//                 warfront Advance, shard zones), and a detachment so the
//                 rest of the party can walk on.
//
//                 The design spine, stated once: the field party is the
//                 DELIVERY MECHANISM for postings the game already has, not a
//                 fourth posting system. A garrison IS the overseer. A held
//                 line IS a shift of Warfront.Advance. Nothing here invents a
//                 consequence that another system does not already resolve.
//
// Layer:          Systems / Strategic
// Collaborators:  FieldWork (the day clock on a job), FieldParty (ParentId,
//                 Work*), ExpeditionAnchors (ReconcilePostings), WorldData,
//                 SupplyCacheSystem (overseer, sieges), Warfront,
//                 CouncilEcho (the ally's echo), ScryInbox (the desk),
//                 PostingThreats (what the world does to a posted force)
//
// Detachments: a detachment is a FieldParty with ParentId set. It stands
// where it was posted, cannot march, cannot take the field, and does not
// count against MaxFieldParties. Recovery is a party standing on its tile
// (Collect) or, on a waystone tile, a free recall. That walk is the leash on
// splitting, and it is deliberate.
// ============================================================

public static class FieldPostings
{
    // ── Kinds ──────────────────────────────────────────────────────────────

    public const string Garrison = "garrison";
    public const string Survey = "survey";
    public const string HoldLine = "hold";
    public const string Siege = "siege";
    public const string Envoy = "envoy";

    /// <summary>Envoy from the field pays half the council's gold: no retinue
    /// to hire, they are already at the gate (ruled 2026-09-24).</summary>
    public const int EnvoyGoldDivisor = 2;

    // ── Starting numbers (spec sections 3, 5; tune in play) ────────────────

    public const int GarrisonSupplies = 2;
    public const int SurveySupplies = 3;
    public const int HoldSupplies = 4;
    public const int SiegeSupplies = 4;

    /// <summary>A survey takes three moons: one ring of the footprint
    /// Explored per moon, so a party that leaves early has still learned
    /// something.</summary>
    public const int SurveyDays = 3 * CalendarState.DaysPerLunation;
    public const int SurveySplintersPerMoon = 6;

    /// <summary>Hold the line: Advance shifts 3 per able member, capped at 9.
    /// Base warfront drift is +6 a moon (KingdomTickSimulation), so two bodies
    /// hold a front still and a full party pushes it back.</summary>
    public const int HoldAdvancePerMember = 3;
    public const int HoldAdvanceCap = 9;
    public const int HoldEchoEveryMoons = 3;

    /// <summary>Lay siege: a player-laid cache siege wanes 15 a moon on its
    /// own (SupplyCacheSystem.PlayerSiegeWane) and only a fight ever closes
    /// it. A posted detachment maintains it: 12 plus 4 per able member, cap
    /// 24, so one body keeps it alive (+1 net), three raise it 9 a moon and
    /// starve the depot out in about six. Storming is faster; this is the
    /// patient road.</summary>
    public const int SiegeAdvanceBase = 12;
    public const int SiegeAdvancePerMember = 4;
    public const int SiegeAdvanceCap = 24;

    // ── What can be done here ──────────────────────────────────────────────

    /// <summary>One row of the posting sheet.</summary>
    public sealed class Option
    {
        public string Kind = "";
        public string Title = "";
        public string Detail = "";
        public int SuppliesPerMoon;
        /// <summary>0 means open-ended: it runs until stopped.</summary>
        public int Days;
        public string ZoneId = "";
        /// <summary>WarfrontSide as int, for Hold the line only.</summary>
        public int Side;
        /// <summary>Gold charged at posting, for Envoy only.</summary>
        public int Gold;
    }

    /// <summary>Every posting valid on the party's tile. Empty when none is,
    /// which is what hides the verb rather than greying it.</summary>
    public static List<Option> OptionsAt(CycleState cycle, FieldParty party)
    {
        var list = new List<Option>();
        var world = cycle?.World;
        if (world == null || party == null || !world.InBounds(party.X, party.Y))
        {
            return list;
        }
        int x = party.X, y = party.Y;
        var tile = world.GetTile(x, y);

        // Garrison: a secured staging point, or a depot that answers to the guild.
        var sp = world.StagingPoints?.Find(s => s.X == x && s.Y == y && s.Source == "Secured");
        if (sp != null)
        {
            list.Add(new Option
            {
                Kind = Garrison,
                Title = "Garrison " + (string.IsNullOrEmpty(sp.Name) ? "this outpost" : sp.Name),
                Detail = "Hold the ground. An ungarrisoned outpost in hostile country can be overrun; "
                       + "a garrisoned one cannot.",
                SuppliesPerMoon = GarrisonSupplies,
                Days = 0,
                ZoneId = ExpeditionAnchors.StagingKeyOf(x, y),
            });
        }
        int poiIndex = tile.PoiIndex;
        WorldPoi poi = poiIndex >= 0 && poiIndex < world.Pois.Count ? world.Pois[poiIndex] : null;
        if (poi != null && poi.Kind == PoiKind.SupplyCache && poi.Discovered)
        {
            string host = SupplyCacheSystem.HostName(cycle, poi);
            if (SupplyCacheSystem.ControllerOf(poi) == SupplyCacheSystem.GuildId)
            {
                bool overseen = !string.IsNullOrEmpty(poi.OverseerCompanionId);
                list.Add(new Option
                {
                    Kind = Garrison,
                    Title = $"Garrison the depot in {host}",
                    Detail = overseen
                        ? "The depot already has an overseer. A garrison here holds the ground only."
                        : $"The first of them oversees the depot: +{SupplyCacheSystem.OverseerYieldBonus} supplies a moon "
                          + "and a stiffer defence against a recapture.",
                    SuppliesPerMoon = GarrisonSupplies,
                    Days = 0,
                    ZoneId = $"poi:{poiIndex}",
                });
            }
            else
            {
                var siege = SupplyCacheSystem.SiegeFor(cycle, poiIndex);
                bool ours = siege != null && siege.AggressorKingdomId == SupplyCacheSystem.GuildId;
                list.Add(new Option
                {
                    Kind = Siege,
                    Title = ours ? $"Maintain the siege of {host}'s depot" : $"Lay siege to the depot in {host}",
                    Detail = $"Starve it out. The siege gains {SiegeAdvanceBase} plus {SiegeAdvancePerMember} per body a moon "
                           + "against its own waning; at 100 the depot is yours. Storming it is faster.",
                    SuppliesPerMoon = SiegeSupplies,
                    Days = 0,
                    ZoneId = $"poi:{poiIndex}",
                });
            }
        }

        // Survey: a discovered shard zone not yet taken or surveyed.
        var zone = world.ShardZoneAt(x, y);
        if (zone != null && zone.Discovered && !zone.ShardCollected && !zone.Surveyed)
        {
            list.Add(new Option
            {
                Kind = Survey,
                Title = $"Survey {(string.IsNullOrEmpty(zone.Name) ? "the shard zone" : zone.Name)}",
                Detail = "Three moons. Each moon lays open another ring of the zone and yields splinters; "
                       + "at the end the gate and the sanctum are known.",
                SuppliesPerMoon = SurveySupplies,
                Days = SurveyDays,
                ZoneId = $"shard:{zone.FragmentKey}",
            });
        }

        // Envoy: a kingdom's seat, or any city where the court already knows
        // us. Only the two UNTARGETED missions are offered from the field;
        // gifts, petitions, courtship and rumour need a courtier chosen, and
        // that picker lives on the council screen where the court is laid out.
        // Choosing a rumour's target for the player would be a decision made
        // for them, so it is not made here.
        var settlement = world.SettlementAt(x, y);
        var save = SaveManager.ActiveSave;
        if (settlement != null && settlement.Tier == SettlementTier.City && save != null
            && !string.IsNullOrEmpty(settlement.KingdomId)
            && cycle.Council?.Courts != null
            && cycle.Council.Courts.TryGetValue(settlement.KingdomId, out var court)
            && (settlement.IsSeat || court.HasContact)
            && court.MissionFreezeLunations <= 0
            && CouncilQueries.MissionAt(settlement.KingdomId) == null
            && cycle.Council.ActiveMissions.Count < CouncilQueries.EnvoyCap(save))
        {
            string courtName = CouncilTick.CourtDisplayName(cycle, settlement.KingdomId);
            foreach (var def in CouncilMissions.All)
            {
                if (def.NeedsTargetCourtier)
                {
                    continue;
                }
                if (def.RequiresContact && !court.HasContact)
                {
                    continue;
                }
                if (court.Band() < def.MinBand)
                {
                    continue;
                }
                if (CouncilQueries.EmbassyTier(save) < def.RequiredEmbassyTier)
                {
                    continue;
                }
                int gold = def.GoldCost / EnvoyGoldDivisor;
                list.Add(new Option
                {
                    Kind = Envoy,
                    Title = $"{def.DisplayName} at {courtName} ({gold}g)",
                    Detail = $"{def.Blurb} {def.Lunations} moon(s). The first of them goes in as envoy; "
                           + "the court feeds them. Half the council's price, since they are already at the gate. "
                           + "If they are seized, the gaol is a place on this map.",
                    SuppliesPerMoon = 0,
                    Days = 0,
                    ZoneId = $"court:{settlement.KingdomId}:{def.Id}",
                    Gold = gold,
                });
            }
        }

        // Hold the line: an open kingdom warfront whose focus is this tile.
        if (cycle.Warfronts != null)
        {
            foreach (var wf in cycle.Warfronts)
            {
                if (wf == null || wf.Closed || wf.IsCacheSiege || !wf.HasFocus
                    || wf.FocusCol != x || wf.FocusRow != y)
                {
                    continue;
                }
                list.Add(new Option
                {
                    Kind = HoldLine,
                    Title = $"Hold the line for {wf.DefenderName}",
                    Detail = $"The front falls back {HoldAdvancePerMember} per body a moon (cap {HoldAdvanceCap}). "
                           + "Every third moon held, their court hears of it.",
                    SuppliesPerMoon = HoldSupplies,
                    Days = 0,
                    ZoneId = $"warfront:{wf.Id}",
                    Side = (int)WarfrontSide.Defend,
                });
                list.Add(new Option
                {
                    Kind = HoldLine,
                    Title = $"Press the attack with {wf.AggressorName}",
                    Detail = $"The front advances {HoldAdvancePerMember} per body a moon (cap {HoldAdvanceCap}). "
                           + "Every third moon, the attacker's court hears of it.",
                    SuppliesPerMoon = HoldSupplies,
                    Days = 0,
                    ZoneId = $"warfront:{wf.Id}",
                    Side = (int)WarfrontSide.Aid,
                });
            }
        }
        return list;
    }

    public static bool IsDetachment(FieldParty p) => p != null && !string.IsNullOrEmpty(p.ParentId);

    /// <summary>Detachments of a party, in list order.</summary>
    public static List<FieldParty> DetachmentsOf(CycleState cycle, FieldParty parent)
    {
        var list = new List<FieldParty>();
        if (cycle?.FieldParties == null || parent == null)
        {
            return list;
        }
        foreach (var p in cycle.FieldParties)
        {
            if (p != null && p.ParentId == parent.Id)
            {
                list.Add(p);
            }
        }
        return list;
    }

    // ── Begin ──────────────────────────────────────────────────────────────

    /// <summary>Post some or all of a party where it stands. A subset becomes
    /// a detachment; the whole party posts itself. Returns the report line,
    /// or null with <paramref name="why"/> set.</summary>
    public static string Begin(CycleState cycle, FieldParty party, Option opt,
                               List<string> memberIds, out string why)
    {
        why = "";
        if (cycle == null || party == null || opt == null)
        {
            why = "Nothing to post.";
            return null;
        }
        if (IsDetachment(party))
        {
            why = "A detachment cannot split again. Collect it first.";
            return null;
        }
        if (!FieldWork.CanBegin(cycle, party, out why))
        {
            return null;
        }
        var members = party.MemberCompanionIds ?? new List<string>();
        memberIds ??= new List<string>();
        memberIds.RemoveAll(id => !members.Contains(id));
        if (memberIds.Count == 0)
        {
            why = "Nobody was chosen to stay.";
            return null;
        }
        if (opt.Kind == Envoy && cycle.Gold < opt.Gold)
        {
            why = $"An envoy needs {opt.Gold} gold to present themselves; the treasury holds {cycle.Gold}.";
            return null;
        }

        FieldParty force;
        bool split = memberIds.Count < members.Count;
        if (split)
        {
            force = MakeDetachment(cycle, party, memberIds);
        }
        else
        {
            force = party;
        }

        int days = opt.Days;
        if (opt.Kind == Survey && Vocations.BestRank(cycle, force, Vocations.Scout) >= 3)
        {
            days = Vocations.ScoutSurveyMoonsR3 * CalendarState.DaysPerLunation;
        }
        string line = FieldWork.Begin(cycle, force, opt.Kind, opt.ZoneId, days);
        if (line == null)
        {
            // Should not happen: CanBegin passed on the parent and a fresh
            // detachment is free by construction. Unwind rather than leave a
            // detachment with no job.
            if (split)
            {
                Merge(cycle, force, party);
            }
            why = "The posting was refused.";
            return null;
        }
        force.WorkSide = opt.Side;
        force.WorkStalled = false;
        force.WorkProgress = 0;
        OnBegin(cycle, force, opt);

        string who = split ? $"{force.Name} ({memberIds.Count} of them)" : party.Name;
        string report = $"{who} {DescribeVerb(opt.Kind)} at ({force.X},{force.Y}), {opt.SuppliesPerMoon} supplies a moon.";
        ScryInbox.Post(cycle, ScryChannel.Note, $"{force.Name}: posted",
                       $"{report} {(opt.Days > 0 ? $"{opt.Days} day(s) of work." : "They hold until recalled.")}",
                       force.Id, "post");
        GD.Print($"[FieldPostings] {report}");
        return report;
    }

    private static FieldParty MakeDetachment(CycleState cycle, FieldParty parent, List<string> memberIds)
    {
        int n = 1;
        string id;
        do
        {
            id = $"{parent.Id}_d{n++}";
        } while (cycle.FieldParties.Exists(p => p != null && p.Id == id));

        var det = new FieldParty
        {
            Id = id,
            Name = $"{parent.Name} detachment",
            ParentId = parent.Id,
            X = parent.X,
            Y = parent.Y,
            AnchorKey = parent.AnchorKey,
            State = FieldPartyState.AtAnchor,
        };
        cycle.FieldParties.Add(det);
        if (cycle.Companions != null)
        {
            foreach (var c in cycle.Companions)
            {
                if (c != null && memberIds.Contains(c.Id))
                {
                    c.Posting = CompanionPosting.Field;
                    c.FieldPartyId = det.Id;
                }
            }
        }
        ExpeditionAnchors.ReconcilePostings(cycle);
        return det;
    }

    /// <summary>Effects that land the moment a posting starts.</summary>
    private static void OnBegin(CycleState cycle, FieldParty force, Option opt)
    {
        var world = cycle.World;
        switch (opt.Kind)
        {
            case Garrison:
            {
                var poi = PoiOf(cycle, opt.ZoneId, out int idx);
                if (poi != null && SupplyCacheSystem.ControllerOf(poi) == SupplyCacheSystem.GuildId
                    && string.IsNullOrEmpty(poi.OverseerCompanionId))
                {
                    // Written directly rather than through AssignOverseer: that
                    // path requires "not in the active party", which a crew
                    // member taken into the field still fails, and it performs
                    // a full save mid-verb. The guild-control check above is
                    // the one that matters.
                    string first = FirstAble(cycle, force);
                    if (!string.IsNullOrEmpty(first))
                    {
                        poi.OverseerCompanionId = first;
                        GD.Print($"[FieldPostings] {first} oversees cache {idx} from the field.");
                    }
                }
                break;
            }
            case Siege:
            {
                var poi = PoiOf(cycle, opt.ZoneId, out int idx);
                if (poi != null && SupplyCacheSystem.SiegeFor(cycle, idx) == null)
                {
                    SupplyCacheSystem.OpenPlayerSiege(cycle, idx);
                    cycle.PendingSiegeReports ??= new List<string>();
                    cycle.PendingSiegeReports.Add(
                        $"Siege laid on the supply cache in {SupplyCacheSystem.HostName(cycle, poi)}.");
                }
                break;
            }
            case Envoy:
            {
                if (!CourtOf(opt.ZoneId, out string kid, out string missionId))
                {
                    break;
                }
                var def = CouncilMissions.Get(missionId);
                string envoyId = FirstAble(cycle, force);
                if (def == null || string.IsNullOrEmpty(envoyId) || cycle.Council == null)
                {
                    break;
                }
                // A Courtier goes for a third of the gold, a Journeyman is a
                // moon faster, a Master is known at the door (2026-09-25).
                var envoyC = cycle.Companions?.Find(x => x != null && x.Id == envoyId);
                int courtier = Vocations.RankIf(envoyC, Vocations.Courtier);
                int gold = courtier >= 1 ? def.GoldCost / Vocations.CourtierGoldDivisor : opt.Gold;
                int moons = courtier >= 2 ? Math.Max(1, def.Lunations - 1) : def.Lunations;
                cycle.Gold = Math.Max(0, cycle.Gold - gold);
                if (courtier >= 3 && cycle.Council.Courts.TryGetValue(kid, out var courtNow))
                {
                    courtNow.HasContact = true;
                }
                // The same record the council screen writes, so CouncilTick
                // resolves it with no idea where it was dispatched from.
                cycle.Council.ActiveMissions.Add(new EnvoyMission
                {
                    CompanionId = envoyId,
                    KingdomId = kid,
                    MissionType = def.Id,
                    LunationsRemaining = moons,
                    TargetCourtierId = "",
                    Recalled = false,
                });
                GD.Print($"[FieldPostings] {envoyId} dispatched from the field to {kid} ({def.Id}, {opt.Gold}g).");
                break;
            }
        }
    }

    // ── Stop, Collect, Recall ──────────────────────────────────────────────

    /// <summary>End a posting cleanly. Overseers step down; a siege loses its
    /// pressure but is not broken; a survey keeps what it has explored.</summary>
    public static string Stop(CycleState cycle, FieldParty force)
    {
        if (cycle == null || force == null || force.State != FieldPartyState.Working)
        {
            return null;
        }
        OnEnd(cycle, force, force.WorkKind, force.WorkZoneId);
        string line = FieldWork.Cancel(cycle, force);
        force.WorkStalled = false;
        force.WorkProgress = 0;
        force.WorkSide = 0;
        return line;
    }

    private static void OnEnd(CycleState cycle, FieldParty force, string kind, string zoneId)
    {
        if (kind == Envoy && CourtOf(zoneId, out string kid, out _))
        {
            // Stopping an envoy posting is a recall: the council's own
            // semantics, one moon home and nothing gained.
            var mission = CouncilQueries.MissionAt(kid);
            if (mission != null && (force.MemberCompanionIds?.Contains(mission.CompanionId) ?? false))
            {
                mission.Recalled = true;
            }
        }
        if (kind == Garrison)
        {
            var poi = PoiOf(cycle, zoneId, out _);
            if (poi != null && !string.IsNullOrEmpty(poi.OverseerCompanionId)
                && (force.MemberCompanionIds?.Contains(poi.OverseerCompanionId) ?? false))
            {
                poi.OverseerCompanionId = "";
            }
        }
    }

    /// <summary>Can this party collect that detachment right now, and if not why.</summary>
    public static bool CanCollect(CycleState cycle, FieldParty party, FieldParty det, out string why)
    {
        why = "";
        if (party == null || det == null || !IsDetachment(det) || det.ParentId != party.Id)
        {
            why = "That is not this party's detachment.";
            return false;
        }
        if (party.State != FieldPartyState.AtAnchor || WorldClock.PartyBusy(cycle, party))
        {
            why = "The party is not standing free.";
            return false;
        }
        if (party.Sortie != null && party.Sortie.IsLive())
        {
            why = "The party is in the field.";
            return false;
        }
        if (party.X != det.X || party.Y != det.Y)
        {
            why = "The party must stand with them.";
            return false;
        }
        return true;
    }

    /// <summary>A detachment on a waystone tile can be recalled to its party
    /// from anywhere, free: the cost was paid raising the stone.</summary>
    public static bool CanRecall(CycleState cycle, FieldParty party, FieldParty det, out string why)
    {
        why = "";
        if (party == null || det == null || !IsDetachment(det) || det.ParentId != party.Id)
        {
            why = "That is not this party's detachment.";
            return false;
        }
        if (party.State != FieldPartyState.AtAnchor || WorldClock.PartyBusy(cycle, party)
            || (party.Sortie != null && party.Sortie.IsLive()))
        {
            why = "The party is not standing free to receive them.";
            return false;
        }
        if (!FieldMarch.IsWaystone(cycle, det.X, det.Y, out _))
        {
            why = "They are not standing on a waystone. Walk to them.";
            return false;
        }
        return true;
    }

    /// <summary>Fold a detachment back into its party. Ends its posting
    /// first. Returns the report line.</summary>
    public static string Collect(CycleState cycle, FieldParty party, FieldParty det)
    {
        if (cycle == null || party == null || det == null)
        {
            return null;
        }
        int n = det.MemberCompanionIds?.Count ?? 0;
        if (det.State == FieldPartyState.Working)
        {
            Stop(cycle, det);
        }
        Merge(cycle, det, party);
        string line = $"{party.Name} take {n} of their own back into the party at ({party.X},{party.Y}).";
        GD.Print($"[FieldPostings] {line}");
        return line;
    }

    private static void Merge(CycleState cycle, FieldParty det, FieldParty into)
    {
        if (cycle.Companions != null)
        {
            foreach (var c in cycle.Companions)
            {
                if (c != null && c.Posting == CompanionPosting.Field && c.FieldPartyId == det.Id)
                {
                    c.FieldPartyId = into.Id;
                }
            }
        }
        cycle.FieldParties.Remove(det);
        ExpeditionAnchors.ReconcilePostings(cycle);
    }

    // ── The new moon ───────────────────────────────────────────────────────

    /// <summary>Every working force pays, is rolled against, and works. Runs
    /// in RunLunationTick BEFORE the kingdom and cache ticks, so a held line
    /// or a maintained siege lands in the same moon it was paid for.</summary>
    public static string TickLunation(CycleState cycle)
    {
        if (cycle?.FieldParties == null)
        {
            return null;
        }
        var lines = new List<string>();
        // Snapshot: a posting can end (Stop) and a detachment is never
        // removed here, but iterate a copy so a future case that does is safe.
        foreach (var force in new List<FieldParty>(cycle.FieldParties))
        {
            if (force == null || force.State != FieldPartyState.Working)
            {
                continue;
            }
            string kind = force.WorkKind;
            int cost = SuppliesFor(cycle, force, kind);

            // 1. Pay, or stall. A stalled posting keeps its place and does no
            // work; it never ends for want of coin (spec 5.2).
            if (cycle.Supplies >= cost)
            {
                cycle.Supplies -= cost;
                force.WorkStalled = false;
            }
            else
            {
                force.WorkStalled = true;
                string stall = $"{force.Name} go hungry: {cost} supplies owed, {cycle.Supplies} in the treasury. "
                             + "They do no work this moon.";
                ScryInbox.Post(cycle, ScryChannel.Note, $"{force.Name}: no supplies", stall, force.Id, "post");
                lines.Add(stall);
                // Threats still roll: hungry people are careless.
                var stalledRoll = PostingThreats.RollForForce(cycle, force);
                if (stalledRoll.Happened) lines.Add(stalledRoll.Report);
                continue;
            }

            // 2. The world's answer.
            var roll = PostingThreats.RollForForce(cycle, force);
            if (roll.Happened)
            {
                lines.Add(roll.Report);
                if (roll.LosesEffect)
                {
                    continue;
                }
            }
            if (force.State != FieldPartyState.Working)
            {
                continue;   // driven off by the roll
            }

            // 3. The work.
            string effect = ApplyMoon(cycle, force, kind, force.WorkZoneId);
            if (!string.IsNullOrEmpty(effect))
            {
                lines.Add(effect);
            }

            // 4. A moon worked (Vocations, 2026-09-25). Only if the posting
            // survived the work: a posting that ended this moon taught nobody.
            if (force.State == FieldPartyState.Working)
            {
                // Physician rank 1: the hurt in this force heal a moon faster.
                if (Vocations.BestRank(cycle, force, Vocations.Physician) >= 1)
                {
                    foreach (var id in force.MemberCompanionIds ?? new List<string>())
                    {
                        var c = cycle.Companions?.Find(x => x != null && x.Id == id);
                        if (c != null && c.InjuredLunationsRemaining > 0)
                        {
                            c.InjuredLunationsRemaining--;
                        }
                    }
                }
                foreach (var promoted in Vocations.RecordMoon(cycle, force, kind))
                {
                    lines.Add(promoted + ".");
                    ScryInbox.Post(cycle, ScryChannel.Note, "A vocation earned", promoted + ".", force.Id, "post");
                }
            }
        }
        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    private static string ApplyMoon(CycleState cycle, FieldParty force, string kind, string zoneId)
    {
        var world = cycle.World;
        switch (kind)
        {
            case Garrison:
            {
                var poi = PoiOf(cycle, zoneId, out int idx);
                if (poi == null)
                {
                    return null;   // an outpost garrison: the overrun roll reads it, nothing to do here
                }
                if (SupplyCacheSystem.ControllerOf(poi) != SupplyCacheSystem.GuildId)
                {
                    // The depot fell under them (FlipCache already wounded the
                    // overseer). The garrison has nothing left to hold.
                    Stop(cycle, force);
                    string lost = $"The depot in {SupplyCacheSystem.HostName(cycle, poi)} is no longer yours. "
                                + $"{force.Name} stand down and await orders.";
                    ScryInbox.Post(cycle, ScryChannel.Sending, $"{force.Name}: the depot fell", lost, force.Id, "post");
                    return lost;
                }
                if (string.IsNullOrEmpty(poi.OverseerCompanionId))
                {
                    string first = FirstAble(cycle, force);
                    if (!string.IsNullOrEmpty(first))
                    {
                        poi.OverseerCompanionId = first;
                    }
                }
                return null;
            }
            case Survey:
            {
                var zone = ZoneOf(cycle, zoneId);
                if (zone == null || zone.ShardCollected)
                {
                    Stop(cycle, force);
                    return $"{force.Name} find nothing left to survey and stand down.";
                }
                // A Scout opens an extra ring a moon, and a Journeyman finds
                // more in it (Vocations, 2026-09-25).
                int scout = Vocations.BestRank(cycle, force, Vocations.Scout);
                force.WorkProgress += 1 + (scout >= 1 ? Vocations.ScoutExtraRing : 0);
                int ring = force.WorkProgress;
                int opened = ExploreRing(world, zone, force.X, force.Y, ring);
                int splinters = SurveySplintersPerMoon + (scout >= 2 ? Vocations.ScoutExtraSplintersR2 : 0);
                cycle.ArcaneSplinters += splinters;
                return $"{force.Name} lay open {opened} tile(s) of {zone.Name} and send home {splinters} splinters.";
            }
            case HoldLine:
            {
                var wf = WarfrontOf(cycle, zoneId);
                if (wf == null || wf.Closed)
                {
                    Stop(cycle, force);
                    string over = $"The front {force.Name} held is over. They stand down and await orders.";
                    ScryInbox.Post(cycle, ScryChannel.Note, $"{force.Name}: the front is quiet", over, force.Id, "post");
                    return over;
                }
                int able = AbleCount(cycle, force);
                int delta = Math.Min(HoldAdvanceCap, able * HoldAdvancePerMember);
                bool defend = force.WorkSide != (int)WarfrontSide.Aid;
                wf.Advance = Math.Clamp(wf.Advance + (defend ? -delta : delta), 0, 100);
                force.WorkProgress++;
                string echo = "";
                if (force.WorkProgress % HoldEchoEveryMoons == 0)
                {
                    string kid = defend ? wf.DefenderKingdomId : wf.AggressorKingdomId;
                    if (CouncilEcho.EmitDeed(cycle, kid, CouncilEcho.SettlementDefended, true, false) != null)
                    {
                        echo = " Word of it reaches their court.";
                    }
                }
                return $"{force.Name} {(defend ? "hold" : "press")} the line at {wf.DefenderName}: "
                     + $"the front {(defend ? "falls back" : "advances")} {delta}, now {wf.Advance}/100.{echo}";
            }
            case Envoy:
            {
                if (!CourtOf(zoneId, out string kid, out _))
                {
                    Stop(cycle, force);
                    return null;
                }
                // The mission is the council's; this posting only watches it.
                // CouncilTick runs AFTER this in the lunation order, so a
                // resolution is noticed one moon later, which is when the
                // envoy walks back out of the court anyway.
                string envoyId = "";
                foreach (var id in force.MemberCompanionIds ?? new List<string>())
                {
                    if (CouncilQueries.IsOnMission(id) || CouncilQueries.IsImprisoned(id))
                    {
                        envoyId = id;
                        break;
                    }
                }
                if (string.IsNullOrEmpty(envoyId))
                {
                    // Nobody on a mission and nobody in gaol: it resolved.
                    Stop(cycle, force);
                    string back = $"{force.Name}'s envoy is back with them at ({force.X},{force.Y}). "
                                + "The herald's report has the outcome.";
                    ScryInbox.Post(cycle, ScryChannel.Note, $"{force.Name}: envoy returns", back, force.Id, "post");
                    return back;
                }
                if (CouncilQueries.IsImprisoned(envoyId))
                {
                    // Seized. They are in the gaol, not in the detachment: the
                    // rescue is a Prison POI the council has already sited.
                    var c = cycle.Companions?.Find(x => x != null && x.Id == envoyId);
                    if (c != null)
                    {
                        c.Posting = CompanionPosting.Campus;
                        c.FieldPartyId = "";
                    }
                    ExpeditionAnchors.ReconcilePostings(cycle);
                    Stop(cycle, force);
                    string seized = $"{c?.Name ?? envoyId} has been seized by the court at "
                                  + $"{CouncilTick.CourtDisplayName(cycle, kid)}. The gaol is on the map; "
                                  + "a party can walk in after them.";
                    ScryInbox.Post(cycle, ScryChannel.Sending, $"{force.Name}: envoy seized", seized, force.Id, "post");
                    return seized;
                }
                return null;
            }
            case Siege:
            {
                var poi = PoiOf(cycle, zoneId, out int idx);
                if (poi == null)
                {
                    Stop(cycle, force);
                    return null;
                }
                if (SupplyCacheSystem.ControllerOf(poi) == SupplyCacheSystem.GuildId)
                {
                    Stop(cycle, force);
                    string won = $"The depot in {SupplyCacheSystem.HostName(cycle, poi)} is yours. "
                               + $"{force.Name} await orders; garrison it to keep it.";
                    ScryInbox.Post(cycle, ScryChannel.Note, $"{force.Name}: the depot is taken", won, force.Id, "post");
                    return won;
                }
                var wf = SupplyCacheSystem.SiegeFor(cycle, idx);
                if (wf == null || wf.AggressorKingdomId != SupplyCacheSystem.GuildId)
                {
                    wf = SupplyCacheSystem.OpenPlayerSiege(cycle, idx);
                }
                int able = AbleCount(cycle, force);
                int gain = Math.Min(SiegeAdvanceCap, SiegeAdvanceBase + able * SiegeAdvancePerMember);
                // A Sapper's pressure sits above the cap: the cap is what bodies
                // can do, the vocation is what one of them knows (2026-09-25).
                int sapper = Vocations.BestRank(cycle, force, Vocations.Sapper);
                gain += sapper >= 2 ? Vocations.SapperAdvanceR2 : sapper >= 1 ? Vocations.SapperAdvanceR1 : 0;
                wf.Advance += gain;
                if (wf.Advance >= 100)
                {
                    // Starved out. The Seize path flips the cache on the spot
                    // and closes the siege, exactly as a won storming would.
                    SupplyCacheSystem.ApplyCacheIntervention(cycle, wf, WarfrontSide.Seize, true);
                    Stop(cycle, force);
                    string fell = $"The depot in {SupplyCacheSystem.HostName(cycle, poi)} is starved out and yours. "
                                + $"{force.Name} await orders; garrison it to keep it.";
                    ScryInbox.Post(cycle, ScryChannel.Sending, $"{force.Name}: the depot falls", fell, force.Id, "post");
                    return fell;
                }
                return $"{force.Name} tighten the siege of the depot in {SupplyCacheSystem.HostName(cycle, poi)}: "
                     + $"+{gain}, now {wf.Advance}/100.";
            }
        }
        return null;
    }

    /// <summary>Called by FieldWork.OnComplete for timed postings.</summary>
    public static string OnComplete(CycleState cycle, FieldParty force, string kind, string zoneId)
    {
        if (kind != Survey)
        {
            return null;
        }
        var zone = ZoneOf(cycle, zoneId);
        if (zone == null)
        {
            return null;
        }
        int opened = 0;
        if (cycle.World != null)
        {
            foreach (var (tx, ty) in zone.Tiles)
            {
                // WorldTile is a STRUCT: write through the array, never a copy.
                if (cycle.World.TryIndex(tx, ty, out int ti)
                    && cycle.World.Tiles[ti].Discovery != TileDiscovery.Explored)
                {
                    cycle.World.Tiles[ti].Discovery = TileDiscovery.Explored;
                    opened++;
                }
            }
        }
        zone.Surveyed = true;
        force.WorkProgress = 0;
        string gate = zone.GuardianCleared ? "The guardian has already fallen." : $"The guardian waits at the gate ({zone.GateX},{zone.GateY}).";
        string body = $"{zone.Name} is surveyed: every tile of it is known. {gate} "
                    + $"The sanctum lies at ({zone.SanctumX},{zone.SanctumY}).";
        ScryInbox.Post(cycle, ScryChannel.Messenger, $"{zone.Name} surveyed", body, force.Id, "post");
        return $"{force.Name} finish the survey of {zone.Name}: {opened} more tile(s) laid open.";
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    /// <summary>The cost this FORCE pays: the base for the kind, lowered by a
    /// vocation at the rank that lowers it (Vocations, 2026-09-25).</summary>
    public static int SuppliesFor(CycleState cycle, FieldParty force, string kind)
    {
        int cost = SuppliesFor(kind);
        if (kind == Garrison)
        {
            if (Vocations.BestRank(cycle, force, Vocations.Quartermaster) >= 2)
            {
                cost = 0;
            }
            else if (Vocations.BestRank(cycle, force, Vocations.Warden) >= 2)
            {
                cost = System.Math.Min(cost, Vocations.WardenGarrisonSupplies);
            }
        }
        else if (kind == Siege && Vocations.BestRank(cycle, force, Vocations.Sapper) >= 3)
        {
            cost = System.Math.Min(cost, Vocations.SapperSiegeSupplies);
        }
        return cost;
    }

    public static int SuppliesFor(string kind) => kind switch
    {
        Garrison => GarrisonSupplies,
        Survey => SurveySupplies,
        HoldLine => HoldSupplies,
        Siege => SiegeSupplies,
        Envoy => 0,
        _ => 0,
    };

    public static string DescribeVerb(string kind) => kind switch
    {
        Garrison => "garrison",
        Survey => "survey",
        HoldLine => "hold the line",
        Siege => "lay siege",
        Envoy => "attend court",
        _ => "work",
    };

    /// <summary>The roster's one line for a working force.</summary>
    public static string Describe(CycleState cycle, FieldParty force)
    {
        if (force == null || force.State != FieldPartyState.Working)
        {
            return "";
        }
        string what = force.WorkKind switch
        {
            Garrison => "garrison",
            Survey => $"survey, ring {force.WorkProgress}",
            HoldLine => force.WorkSide == (int)WarfrontSide.Aid ? "pressing the line" : "holding the line",
            Siege => "besieging the depot",
            Envoy => "envoy at court",
            _ => FieldWork.Describe(force),
        };
        string stalled = force.WorkStalled ? "  ·  stalled: no supplies" : "";
        string left = force.WorkDaysTotal > 0 ? $"  ·  {force.WorkDaysLeft} day(s) left" : "";
        return $"{what}{left}{stalled}";
    }

    public static int AbleCount(CycleState cycle, FieldParty force)
    {
        int n = 0;
        if (cycle?.Companions == null || force?.MemberCompanionIds == null)
        {
            return 0;
        }
        foreach (var id in force.MemberCompanionIds)
        {
            var c = cycle.Companions.Find(x => x != null && x.Id == id);
            if (c != null && c.IsRecruited && !c.IsPermadead && !c.IsInjured)
            {
                n++;
            }
        }
        return n;
    }

    private static string FirstAble(CycleState cycle, FieldParty force)
    {
        if (cycle?.Companions == null || force?.MemberCompanionIds == null)
        {
            return "";
        }
        foreach (var id in force.MemberCompanionIds)
        {
            var c = cycle.Companions.Find(x => x != null && x.Id == id);
            if (c != null && c.IsRecruited && !c.IsPermadead && !c.IsInjured)
            {
                return c.Id;
            }
        }
        return "";
    }

    private static WorldPoi PoiOf(CycleState cycle, string zoneId, out int index)
    {
        index = -1;
        if (string.IsNullOrEmpty(zoneId) || !zoneId.StartsWith("poi:"))
        {
            return null;
        }
        if (!int.TryParse(zoneId.Substring(4), out index))
        {
            index = -1;
            return null;
        }
        var world = cycle?.World;
        if (world == null || index < 0 || index >= world.Pois.Count)
        {
            index = -1;
            return null;
        }
        return world.Pois[index];
    }

    private static ShardZone ZoneOf(CycleState cycle, string zoneId)
    {
        if (string.IsNullOrEmpty(zoneId) || !zoneId.StartsWith("shard:") || cycle?.World?.ShardZones == null)
        {
            return null;
        }
        string key = zoneId.Substring(6);
        return cycle.World.ShardZones.Find(z => z != null && z.FragmentKey == key);
    }

    private static Warfront WarfrontOf(CycleState cycle, string zoneId)
    {
        if (string.IsNullOrEmpty(zoneId) || !zoneId.StartsWith("warfront:") || cycle?.Warfronts == null)
        {
            return null;
        }
        string id = zoneId.Substring(9);
        return cycle.Warfronts.Find(w => w != null && w.Id == id);
    }

    private static bool CourtOf(string zoneId, out string kingdomId, out string missionId)
    {
        kingdomId = "";
        missionId = "";
        if (string.IsNullOrEmpty(zoneId) || !zoneId.StartsWith("court:"))
        {
            return false;
        }
        var parts = zoneId.Split(':');
        if (parts.Length < 3)
        {
            return false;
        }
        kingdomId = parts[1];
        missionId = parts[2];
        return !string.IsNullOrEmpty(kingdomId);
    }

    /// <summary>A party standing on a Veiled Concord node makes contact. The
    /// node is Discovered, the council's ConcordContacted flag flips, and
    /// nothing else happens here: the Concord's verbs live on the campus
    /// panel. Returns a line the first time only.</summary>
    public static string ContactConcordAt(CycleState cycle, int x, int y)
    {
        var world = cycle?.World;
        if (world == null || cycle.Council == null || !world.InBounds(x, y))
        {
            return null;
        }
        int idx = world.GetTile(x, y).PoiIndex;
        if (idx < 0 || idx >= world.Pois.Count || world.Pois[idx].Kind != PoiKind.Concord)
        {
            return null;
        }
        world.Pois[idx].Discovered = true;
        if (cycle.Council.ConcordContacted)
        {
            return null;
        }
        cycle.Council.ConcordContacted = true;
        string line = "A door with no sign, and a price list behind it. The Veiled Concord will deal with you now.";
        ScryInbox.Post(cycle, ScryChannel.Messenger, "The Veiled Concord", line, "", "concord");
        GD.Print("[FieldPostings] Concord contacted by foot.");
        return line;
    }

    /// <summary>Explore the zone's footprint tiles within <paramref name="ring"/>
    /// hexes of the party. Returns how many newly opened.</summary>
    private static int ExploreRing(WorldData world, ShardZone zone, int x, int y, int ring)
    {
        int opened = 0;
        if (world == null || zone?.Tiles == null)
        {
            return 0;
        }
        foreach (var (tx, ty) in zone.Tiles)
        {
            if (!world.InBounds(tx, ty) || world.HexDistance(x, y, tx, ty) > ring)
            {
                continue;
            }
            // WorldTile is a STRUCT: write through the array, never a copy.
            if (world.TryIndex(tx, ty, out int ti) && world.Tiles[ti].Discovery != TileDiscovery.Explored)
            {
                world.Tiles[ti].Discovery = TileDiscovery.Explored;
                opened++;
            }
        }
        return opened;
    }
}
