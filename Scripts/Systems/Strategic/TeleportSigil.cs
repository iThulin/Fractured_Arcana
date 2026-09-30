using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// TeleportSigil.cs
//
// Purpose:        The Teleport Sigil (campus_building_upgrades_design_v1
//                 §10, corrected design, ruled 2026-09-29). Parties can
//                 already step from anywhere to the parked castle, a raised
//                 waystone or the dock for free (FieldMarch). The Sigil
//                 extends that network, and building it opens the campus to
//                 a portal strike.
//                   T1 Waygate       parties may also step to the guild's
//                                    own walk-only anchors: outposts,
//                                    secured sites, claimed shard zones,
//                                    and held supply caches.
//                                    The portal vector opens (sieges).
//                   T2 Recall        a detachment may be recalled from
//                                    anywhere, not only from a waystone;
//                                    a marching party may abandon the march
//                                    and step home to the dock.
//                   T3 doctrine      Gate Network: any settlement of an
//                                    Allied kingdom is a waystone too.
//                                    Sally Port: the wizard's summons to a
//                                    field party costs no days (Administrator).
//
//                 Portal strikes (the siege spec §10 asked for):
//                   WHO    a kingdom Hostile to the guild learns the Sigil's
//                          pattern: +2 a moon while Hostile, +1 while
//                          Unfriendly, -1 otherwise (floor 0). At 4 it knows
//                          the pattern and the desk is warned.
//                   WHEN   each moon, a kingdom that knows the pattern
//                          strikes 40% of the time (seeded per moon and
//                          kingdom, so a reload cannot reroll it). One strike
//                          owed at a time.
//                   FIGHT  the home city compiled around the Sigil
//                          (CityBattlemapCompiler.CompilePortalStrike),
//                          Siege-tier roster from the attacker's region,
//                          objective: survive 8 rounds. The defenders are the
//                          wizard and the home party.
//                   WIN    the usual fight pay; the attacker's knowledge of
//                          the pattern resets.
//                   LOSS   the Sigil and one other sited, non-foundational
//                          building each take StrikeDamage integrity
//                          (BuildingSaveData.ApplyDamage: at 0 the existing
//                          destroy rule applies); a fifth of the guild's gold
//                          is carried off; knowledge resets.
//                   SEAL   refusing the fight collapses the rift: the Sigil
//                          alone takes StrikeDamage, knowledge resets.
//                 Damage is repaired at the campus for materials
//                 (CampusConstruction.TryRepair).
//
//                 Corruption (design §11, 2026-09-29): the strike is an
//                 attempt to foul the guild's land. In the fight, each
//                 building nearby has a foundation the attackers can raze
//                 and their ritualists channel blight into; a LOSS also
//                 fouls the Sigil's own ground. A blighted Sigil leaks its
//                 pattern (learning doubles) and, at T3, opens its gates on
//                 Allied and Friendly cities with no charter. The Undercroft
//                 sends early word at knowledge 2 (T1+) and slows learning by
//                 one (T2+). Destroyed buildings leave Rubble (CampusBlight).
// Layer:          System (strategic)
// Collaborators:  FieldMarch (IsWaystone, CanTeleportTo), FieldPostings
//                 (CanRecall), CouncilQueries.StanceFor, SupplyCacheSystem,
//                 CycleState (PortalKnowledge, PendingPortalStrikeKingdomId,
//                 PortalStrikeLaunched), StrategicView (offer, launch,
//                 return), Charters (Gate Network).
// ============================================================

/// <summary>The Teleport Sigil's gates, travel targets and portal strikes. Stateless.</summary>
public static class TeleportSigil
{
    public const string Id = "teleport_sigil";
    public const string GateNetwork = "gate_network";
    public const string SallyPort = "sally_port";

    // ── Portal strike tuning (starting values) ───────────────────────────
    public const int KnowledgeToStrike = 4;
    public const int HostileLearn = 2;
    public const int UnfriendlyLearn = 1;
    public const int StrikeChancePercent = 40;
    /// <summary>Integrity a lost or sealed strike costs a building (flat 20 max).</summary>
    public const int StrikeDamage = 10;
    public const int GoldTakenPercent = 20;
    public const int StrikeRounds = 8;
    /// <summary>Pattern knowledge at which the Undercroft (T1+) sends early word.</summary>
    public const int UndercroftWarnAt = 2;
    public const string UndercroftId = "undercroft";

    // ── Gates ────────────────────────────────────────────────────────────

    /// <summary>The Sigil's tier, 0 when unbuilt or unsited.</summary>
    public static int Tier(GuildSaveData save)
    {
        if (save?.Buildings == null)
        {
            return 0;
        }
        foreach (var b in save.Buildings)
        {
            if (b.Id == Id)
            {
                return b.IsFunctional ? b.Tier : 0;
            }
        }
        return 0;
    }

    public static bool RecallAnywhere(GuildSaveData save) => Tier(save) >= 2;

    /// <summary>The Sigil's tier when built and sited, working or not (an overrun
    /// Sigil still stands to be struck through). 0 when unbuilt or unsited.</summary>
    public static int SitedTier(GuildSaveData save)
    {
        var b = CampusBlight.Find(save, Id);
        return b != null && b.Tier > 0 && b.IsPlaced ? b.Tier : 0;
    }

    /// <summary>The Undercroft's tier (0 when unbuilt, unsited or overrun).</summary>
    public static int UndercroftTier(GuildSaveData save)
    {
        var b = CampusBlight.Find(save, UndercroftId);
        return b != null && b.IsFunctional ? b.Tier : 0;
    }

    /// <summary>The guild dock's tile (the "Start" staging point), or null.</summary>
    public static (int x, int y)? DockTile(CycleState cycle)
    {
        if (cycle?.World?.StagingPoints == null)
        {
            return null;
        }
        foreach (var sp in cycle.World.StagingPoints)
        {
            if (sp != null && sp.Source == "Start")
            {
                return (sp.X, sp.Y);
            }
        }
        return null;
    }

    public static bool GateNetworkActive(GuildSaveData save) => Charters.IsActive(save, Id, GateNetwork);

    /// <summary>Is this tile a waystone only because of the Sigil? FieldMarch.IsWaystone
    /// asks after its own three (castle, raised waystone, dock).</summary>
    public static bool IsSigilWaystone(CycleState cycle, int x, int y, out string what)
    {
        what = "";
        var save = SaveManager.ActiveSave;
        if (cycle?.World == null || Tier(save) < 1)
        {
            return false;
        }
        var world = cycle.World;

        // T1 Waygate: the guild's own walk-only anchors.
        if (world.StagingPoints != null)
        {
            foreach (var sp in world.StagingPoints)
            {
                if (sp == null || !sp.Available || sp.X != x || sp.Y != y)
                {
                    continue;
                }
                if (sp.Source == "Outpost" || sp.Source == "Secured" || sp.Source == "Shard")
                {
                    what = string.IsNullOrEmpty(sp.Name) ? "a guild outpost" : sp.Name;
                    return true;
                }
            }
        }
        var poi = world.PoiAt(x, y);
        if (poi != null && poi.Kind == PoiKind.SupplyCache
            && SupplyCacheSystem.ControllerOf(poi) == SupplyCacheSystem.GuildId)
        {
            what = "the guild's supply cache";
            return true;
        }

        // T3 Gate Network: an Allied kingdom's settlements. A BLIGHTED Sigil at
        // T3 (campus corruption, §11c) needs no charter: the rift is loose, and
        // it opens on Friendly kingdoms' cities too.
        bool looseRift = Tier(save) >= 3 && CampusBlight.IsBlighted(save, Id);
        if ((GateNetworkActive(save) || looseRift) && world.Settlements != null)
        {
            foreach (var s in world.Settlements)
            {
                if (s == null || s.IsGuildHome || s.CenterX != x || s.CenterY != y)
                {
                    continue;
                }
                var stance = CouncilQueries.StanceFor(cycle, s.KingdomId);
                if (stance == KingdomStance.Allied || (looseRift && stance == KingdomStance.Friendly))
                {
                    what = string.IsNullOrEmpty(s.Name) ? "an allied city" : s.Name;
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>Is this tile the guild dock (the "Start" staging point)?</summary>
    public static bool IsDock(CycleState cycle, int x, int y)
    {
        if (cycle?.World?.StagingPoints == null)
        {
            return false;
        }
        foreach (var sp in cycle.World.StagingPoints)
        {
            if (sp != null && sp.Source == "Start" && sp.X == x && sp.Y == y)
            {
                return true;
            }
        }
        return false;
    }

    // ── Portal strikes ───────────────────────────────────────────────────

    /// <summary>Lunation tick: kingdoms learn or forget the pattern, and one that
    /// knows it may strike. Returns report lines. Non-throwing by construction
    /// (every lookup null-checked).</summary>
    public static List<string> TickLunation(CycleState cycle, GuildSaveData save)
    {
        var lines = new List<string>();
        if (cycle?.Kingdoms == null)
        {
            return lines;
        }
        cycle.PortalKnowledge ??= new Dictionary<string, int>();

        // The circle only has to STAND for its pattern to be learned: an overrun
        // Sigil (campus corruption) no longer carries the guild's parties, but the
        // rift is still there to be opened from the far side.
        if (SitedTier(save) < 1)
        {
            // No circle, no pattern to learn. What was learned fades with it.
            cycle.PortalKnowledge.Clear();
            return lines;
        }

        int lunation = cycle.Calendar?.CurrentLunation ?? 1;
        // A blighted Sigil (§11c) leaks its pattern: learning doubles. The
        // Undercroft's handlers (T2+) feed false patterns back and slow it by one.
        bool leaking = CampusBlight.IsBlighted(save, Id);
        int undercroft = UndercroftTier(save);
        var knowers = new List<string>();
        foreach (var kv in cycle.Kingdoms)
        {
            string kid = kv.Key;
            var stance = CouncilQueries.StanceFor(cycle, kid);
            cycle.PortalKnowledge.TryGetValue(kid, out int before);
            int learn = stance switch
            {
                KingdomStance.Hostile => HostileLearn,
                KingdomStance.Unfriendly => UnfriendlyLearn,
                _ => 0,
            };
            if (learn > 0)
            {
                if (leaking)
                {
                    learn *= 2;
                }
                if (undercroft >= 2)
                {
                    learn = Math.Max(0, learn - 1);
                }
            }
            int after = learn > 0 ? before + learn
                      : stance == KingdomStance.Hostile || stance == KingdomStance.Unfriendly ? before
                      : Math.Max(0, before - 1);
            after = Math.Min(after, KnowledgeToStrike);
            cycle.PortalKnowledge[kid] = after;

            // The Undercroft's early word (T1+): a kingdom halfway to the pattern.
            if (undercroft >= 1 && before < UndercroftWarnAt && after >= UndercroftWarnAt && after < KnowledgeToStrike)
            {
                string warn = $"Word from the Undercroft: agents of {kv.Value.DisplayName} are studying the Teleport "
                            + "Sigil's pattern. They are halfway to it. Mend relations, or be ready at the rift.";
                ScryInbox.Post(cycle, ScryChannel.Note, "The Sigil is being studied", warn, "", "portal");
                lines.Add(warn);
            }

            if (before < KnowledgeToStrike && after >= KnowledgeToStrike)
            {
                string line = $"{kv.Value.DisplayName} has learned the pattern of the "
                            + "Teleport Sigil. They can open it from their side now.";
                ScryInbox.Post(cycle, ScryChannel.Messenger, "The Sigil's pattern is known", line, "", "portal");
                lines.Add(line);
            }
            if (after >= KnowledgeToStrike)
            {
                knowers.Add(kid);
            }
        }

        if (!string.IsNullOrEmpty(cycle.PendingPortalStrikeKingdomId) || knowers.Count == 0)
        {
            return lines;   // one strike owed at a time
        }
        knowers.Sort(string.CompareOrdinal);
        foreach (var kid in knowers)
        {
            var rng = RngFor(cycle, kid, lunation);
            if (rng.Next(100) >= StrikeChancePercent)
            {
                continue;
            }
            cycle.PendingPortalStrikeKingdomId = kid;
            cycle.PortalStrikeLaunched = false;
            string name = cycle.Kingdoms.TryGetValue(kid, out var k) ? k.DisplayName : kid;
            lines.Add($"The Teleport Sigil burns open from the far side. Soldiers of {name} are coming through.");
            break;
        }
        SaveManager.MarkDirty();
        return lines;
    }

    /// <summary>Resolve a strike's outcome on the guild. <paramref name="outcome"/>:
    /// "won", "lost" or "sealed". Clears the owed strike and the attacker's
    /// knowledge. Returns the report line.</summary>
    public static string Resolve(CycleState cycle, GuildSaveData save, string outcome, string attackerName)
    {
        if (cycle == null || save == null)
        {
            return "";
        }
        string kid = cycle.PendingPortalStrikeKingdomId;
        cycle.PendingPortalStrikeKingdomId = "";
        cycle.PortalStrikeLaunched = false;
        if (!string.IsNullOrEmpty(kid) && cycle.PortalKnowledge != null)
        {
            cycle.PortalKnowledge[kid] = 0;
        }

        string line;
        if (outcome == "won")
        {
            line = $"The rift is sealed from our side. {attackerName} is thrown back through, and the "
                 + "pattern they stole is useless to them now.";
        }
        else if (outcome == "sealed")
        {
            string hit = DamageBuilding(save, Id);
            line = $"The circle is broken by our own hands before {attackerName} can step through. " + hit;
        }
        else
        {
            // A held rift is a finished ritual: the ground under the Sigil is
            // fouled (campus corruption, §11) before anything is broken.
            string fouled = CampusBlight.CorruptBuildingLand(save, Id, 1);
            string sigil = DamageBuilding(save, Id);
            string other = DamageRandomOther(save, cycle);
            int taken = save.Gold * GoldTakenPercent / 100;
            save.Gold -= taken;
            line = $"{attackerName} held the campus long enough to loot it. {fouled} {sigil} {other}"
                 + (taken > 0 ? $" {taken} gold is gone from the treasury." : "");
        }
        SaveManager.MarkDirty();
        GD.Print($"[PortalStrike] {outcome}: {line}");
        return line.Trim();
    }

    /// <summary>Apply what the attackers did to the ground during the fight
    /// (SiegeBlightReport, filled by CombatManager.SiegeBlight): each razed
    /// foundation damages its building and fouls its ground one step, and each
    /// finished ritual fouls it one step more. Win or lose, what they did stands.
    /// Clears the report. Returns the report text, "" when nothing happened.</summary>
    public static string ApplySiegeBlight(GuildSaveData save)
    {
        if (save == null || !SiegeBlightReport.Active)
        {
            SiegeBlightReport.Clear();
            return "";
        }
        // Rituals first, then razes, so each building's report line reads in one
        // place: the ground is fouled by rituals and razing together, then the
        // stone takes the damage.
        var parts = new List<string>();
        var steps = new Dictionary<string, int>(SiegeBlightReport.Fouled);
        foreach (var id in SiegeBlightReport.Razed)
        {
            steps.TryGetValue(id, out int n);
            steps[id] = n + 1;
        }
        var ids = new List<string>(steps.Keys);
        ids.Sort(string.CompareOrdinal);
        foreach (var id in ids)
        {
            string fouled = CampusBlight.CorruptBuildingLand(save, id, steps[id]);
            string hit = SiegeBlightReport.Razed.Contains(id) ? DamageBuilding(save, id, StrikeDamage) : "";
            string both = $"{fouled} {hit}".Trim();
            if (!string.IsNullOrEmpty(both))
            {
                parts.Add(both);
            }
        }
        SiegeBlightReport.Clear();
        if (parts.Count > 0)
        {
            SaveManager.MarkDirty();
        }
        return string.Join(" ", parts);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static string DamageBuilding(GuildSaveData save, string buildingId)
        => DamageBuilding(save, buildingId, StrikeDamage);

    /// <summary>Damage a standing building. On destruction its campus effects
    /// leave with it and its footprint becomes Rubble (CampusBlight). Returns the
    /// report line, "" when there is no standing building by that id.</summary>
    public static string DamageBuilding(GuildSaveData save, string buildingId, int amount)
    {
        if (save?.Buildings == null)
        {
            return "";
        }
        foreach (var b in save.Buildings)
        {
            if (b.Id != buildingId || b.Tier <= 0)
            {
                continue;
            }
            bool destroyed = b.ApplyDamage(amount);
            if (destroyed)
            {
                // Its campus effects (party size and the like) leave with it,
                // and the ground it stood on is broken stone until cleared.
                CampusBlight.MarkRubble(save, b);
                CampusBlight.Recompute(save);
                BuildingEffectApplier.ApplyCampusEffects(save);
            }
            return destroyed
                ? $"{b.Name} is destroyed."
                : $"{b.Name} is damaged ({b.CurrentIntegrity}/{b.MaxIntegrity}).";
        }
        return "";
    }

    private static string DamageRandomOther(GuildSaveData save, CycleState cycle)
    {
        var candidates = new List<BuildingSaveData>();
        foreach (var b in save.Buildings)
        {
            if (b.Id == Id || !b.IsFunctional)
            {
                continue;
            }
            var t = BuildingDatabase.GetTemplate(b.Id);
            if (t == null || t.IsFoundational)
            {
                continue;
            }
            candidates.Add(b);
        }
        if (candidates.Count == 0)
        {
            return "";
        }
        candidates.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        var rng = RngFor(cycle, "loss", cycle.Calendar?.CurrentLunation ?? 1);
        return DamageBuilding(save, candidates[rng.Next(candidates.Count)].Id);
    }

    private static Random RngFor(CycleState cycle, string salt, int lunation)
    {
        unchecked
        {
            int seed = cycle.WorldSeed;
            seed = (seed * 397) ^ lunation;
            foreach (char ch in salt ?? "")
            {
                seed = (seed * 31) ^ ch;
            }
            return new Random(seed);
        }
    }
}
