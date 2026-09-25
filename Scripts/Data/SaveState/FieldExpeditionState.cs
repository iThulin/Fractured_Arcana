using System.Collections.Generic;
using System.Text.Json.Serialization;

// ============================================================
// FieldExpeditionState.cs
//
// Purpose:        Expedition v2 (split forces) data layer. The castle
//                 stops being the only thing on the map: it parks and
//                 becomes a mobile ANCHOR, while a separate FIELD PARTY
//                 travels between known anchors (cities, shard zones,
//                 secured outposts, built waypoints, the parked castle)
//                 and dives the POIs around them.
//
//                 Pure data, no Godot nodes, no behaviour. Everything
//                 here serializes into CycleState as additive fields
//                 with safe defaults, so pre-feature saves load clean
//                 and are backfilled lazily by ExpeditionAnchors.
//                 No SaveManager version bump (house rule: additive
//                 save fields, lazy backfill).
//
//                 Deliberately NOT here:
//                   - a PartyLoadout equipment container. ArmoryData
//                     .Loadouts is already keyed per companion id, so
//                     equipment follows the person into whichever force
//                     they are posted to. A party owns consumables and
//                     a roster, nothing else.
//                   - waypoint affixes. Stubbed out of v1 on purpose;
//                     add the field when the affix table exists.
// Layer:          Data
// Collaborators:  CycleState.cs (stores all of this),
//                 ExpeditionAnchors.cs (the union view + backfill),
//                 CompanionDefinition.cs (Posting / FieldPartyId),
//                 WorldData.cs (StagingPoint, ShardZone, WorldPoi),
//                 FieldExpeditionSaveAssert.cs (round-trip proof)
// See:            docs/expedition_bastion_and_field_party_handoff_v1.md,
//                 docs/expedition_bastion_pressure_test_2026-09-21.md,
//                 docs/mobile_fortress_expedition_spec_v1.md (the crew
//                 stations this splits away from)
// ============================================================

/// <summary>Where a companion is posted. The castle crew staffs the
/// CrewStations that drive fortress characteristics; the field party
/// walks into POIs; campus companions are doing neither this lunation.
/// Default is Campus so a pre-feature save deserializes to "posted
/// nowhere" and ExpeditionAnchors.BackfillPostings can reconstruct the
/// crew from the existing ActivePartyCompanionIds list.</summary>
public enum CompanionPosting
{
    Campus = 0,
    Crew = 1,
    Field = 2,
}

/// <summary>What a field party may travel to. Staging covers the existing
/// StagingPoint list (start, secured outposts, settlements, seats), so the
/// 29 existing StagingPoints call sites keep their meaning untouched and
/// this enum only classifies the union view on top of them.</summary>
public enum AnchorKind
{
    Staging = 0,
    ShardZone = 1,
    CastlePark = 2,
    Built = 3,
}

/// <summary>What a field party is doing right now.</summary>
public enum FieldPartyState
{
    AtAnchor = 0,
    Travelling = 1,
    Deployed = 2,
    Returning = 3,
    /// <summary>Doing a multi-lunation job where it stands (a shard zone, a
    /// depot, a city). Resolved by the lunation tick through FieldWork, never
    /// by the expedition scene, which is one lunation of walking and nothing
    /// longer. Appended 2026-09-23; the enum is serialized by value.</summary>
    Working = 4,
}

/// <summary>A BUILT waypoint: the consumable kind the castle conjures over a
/// discovered POI. Permanent anchors (cities, shard zones, secured outposts)
/// are NOT waypoints and are never stored here; they come from the world
/// tables through ExpeditionAnchors. Only the built ones carry charges.</summary>
public class Waypoint
{
    /// <summary>Stable key, "built:x,y". Matches AnchorRef.Key so a field
    /// party's AnchorKey can name a built waypoint the same way it names a
    /// city.</summary>
    public string Key = "";

    /// <summary>Index into WorldData.Pois, or -1 if the waypoint covers bare
    /// ground. Mirrors the WorldTile.PoiIndex convention rather than inventing
    /// a string PoiId that would need its own lookup.</summary>
    public int PoiIndex = -1;

    public int X = -1;
    public int Y = -1;

    /// <summary>Dives left before the waypoint closes. Seeded from the castle's
    /// module loadout at creation (ruling: charges come from castle upgrades;
    /// CastleModules is the upgrade ladder that actually exists).</summary>
    public int Charges = 1;

    /// <summary>Lunation the waypoint was conjured on, for the run log and for
    /// any later expiry rule.</summary>
    public int CreatedLunation = 0;

    /// <summary>Lunation the waypoint closes on, or 0 for "never expires".
    /// Defaults to 0 deliberately: the handoff listed expiry fields, but a
    /// rot timer is the chore the redesign exists to remove, so the schema
    /// carries it and the behaviour does not use it until ruled otherwise.
    /// See the pressure test, finding 7.</summary>
    public int ExpiresAtLunation = 0;

    /// <summary>"castle" or "scripted".</summary>
    public string CreatedBy = "castle";

    [JsonIgnore]
    public bool IsSpent => Charges <= 0;

    /// <summary>The canonical key for a built waypoint at a coordinate. One
    /// place builds the string so nothing drifts.</summary>
    public static string KeyOf(int x, int y) => $"built:{x},{y}";
}

/// <summary>One force of companions operating away from the castle. The list
/// lives on CycleState so the turn loop can iterate parties from day one even
/// while MaxFieldParties is 1, making extra parties a data change rather than
/// a refactor.</summary>
public class FieldParty
{
    /// <summary>Stable id, "field_1". Referenced by Companion.FieldPartyId.</summary>
    public string Id = "field_1";

    public string Name = "Field Party";

    /// <summary>Companion ids posted to this party. The authoritative posting
    /// is Companion.Posting plus Companion.FieldPartyId; this list is the
    /// ordered view the UI reads. ExpeditionAnchors.ReconcilePostings keeps
    /// the two agreeing and treats the Companion fields as the source.</summary>
    public List<string> MemberCompanionIds = new();

    public FieldPartyState State = FieldPartyState.AtAnchor;

    /// <summary>Anchor the party is sitting at, or empty if it has none yet.</summary>
    public string AnchorKey = "";

    /// <summary>Anchor the party is travelling to, empty when not travelling.</summary>
    public string DestinationAnchorKey = "";

    /// <summary>Where they are going, as a coordinate.
    ///
    /// <para>Found on review, 2026-09-21: arrival originally resolved the
    /// destination by looking DestinationAnchorKey up in the live anchor list.
    /// That fails in exactly the cases that matter. The parked castle appears in
    /// that list under a "castle:" key while its staging point is "staging:", so
    /// a march to the fortress resolved to null and the party arrived without
    /// moving. A built waypoint that spent its last charge mid-journey vanishes
    /// from the list entirely, with the same result. A destination is a place,
    /// not a row in a table that may have been rebuilt since.</para></summary>
    public int DestX = -1;
    public int DestY = -1;

    /// <summary>Phases of travel still owed before arrival. Travel between
    /// anchors is abstracted: no hex walking overland, only the POI area is
    /// walked.</summary>
    public int TravelPhasesRemaining = 0;

    /// <summary>World position. Equals the anchor tile while AtAnchor; during
    /// travel it stays at the origin anchor (travel is abstract, the party is
    /// not rendered mid-route). -1,-1 before the party is first placed.</summary>
    public int X = -1;
    public int Y = -1;

    /// <summary>Consumable ItemDefinition ids carried on this sortie. Worn
    /// equipment is NOT here: ArmoryData.Loadouts already binds items to the
    /// companion, so it follows them into whichever force they join.</summary>
    public List<string> CarriedConsumableIds = new();

    /// <summary>Spoils the party is carrying out of the field but has not banked.
    /// Ruled 2026-09-21: a field party carries the castle's hold back. This is
    /// where it rides between the pickup and the bank.
    ///
    /// <para>The same shape as CastleHold on purpose, because it holds the same
    /// thing and a second bespoke bundle type would only mean two Add methods to
    /// keep in step. Loaded when the party departs the castle's park; banked on
    /// arrival at a staging anchor, which is a place with people and a waystone.
    /// A shard zone or a built waypoint is neither, so neither banks.</para></summary>
    public CastleHold Carrying = new();

    /// <summary>The job in hand while State is Working (2026-09-23 skeleton).
    /// Kind names the verb ("survey", "salvage", "trade"; the switch in
    /// FieldWork.OnComplete grows one case per zone type), ZoneId names the
    /// place in whatever terms that zone type uses, and the two counters are
    /// the clock. Nothing else about the work is stored yet: what it costs and
    /// what it yields are the zone rulings still to be made.</summary>
    public string WorkKind = "";
    public string WorkZoneId = "";
    public int WorkDaysLeft = 0;
    public int WorkDaysTotal = 0;

    /// <summary>Field Party v1 (2026-09-24, docs/field_party_task_force_spec_v1):
    /// a DETACHMENT is a FieldParty with this set to the id of the party it was
    /// split from. It stands where it was posted, cannot march or take the
    /// field, and does not count against MaxFieldParties. Empty for a party.
    /// Same struct on purpose: ReconcilePostings, the roster, SetPieces and the
    /// save assert all iterate FieldParties already, so a detachment is
    /// reachable everywhere the moment its id exists.</summary>
    public string ParentId = "";

    /// <summary>True when the treasury could not pay this posting's Supplies at
    /// the last new moon. A stalled posting keeps its place and does no work;
    /// it never ends for want of coin (spec 5.2).</summary>
    public bool WorkStalled = false;

    /// <summary>Accumulator for postings that count something across moons: a
    /// survey's explored rings, a held line's moons toward the ally's echo.</summary>
    public int WorkProgress = 0;

    /// <summary>WarfrontSide as an int for Hold the line (0 Defend, 2 Aid).
    /// Int rather than the enum so the save carries no new enum converter.</summary>
    public int WorkSide = 0;

    /// <summary>Absolute day the party is next free to be ordered; the days a
    /// dive cost, charged when it ends. 0 means free.</summary>
    public int BusyUntilDay = 0;

    /// <summary>Days walked toward the next tile of a march. The party steps
    /// one tile every FieldMarch.DaysPerTile days; this is the remainder.</summary>
    public int TravelDayAccum = 0;

    /// <summary>This party's frozen run, when it is in the field and the table
    /// is currently watching something else.</summary>
    public SortieState Sortie = new();

    [JsonIgnore]
    public bool IsAway => State != FieldPartyState.AtAnchor;
}

/// <summary>The expedition turn budget for one lunation. Ruling 2026-09-21:
/// a lunation buys ONE castle move plus K field dives. This keeps map reveal
/// on the 12-lunation clock (the single_world_refactor_v2 section 5 pacing
/// keystone) while holding POI throughput near what a radius-12 sortie used
/// to deliver. See the pressure test, finding 2, for the arithmetic.</summary>
public class ExpeditionTurnState
{
    /// <summary>Lunation this budget belongs to. BeginLunation resets the
    /// counters when the calendar moves past it.</summary>
    public int Lunation = 0;

    public bool CastleMoveSpent = false;

    public int DivesSpent = 0;

    /// <summary>Starting value for K. Exported as a field, not a const, so
    /// playtest can retune it from the debug panel without a rebuild.</summary>
    public int DivesPerLunation = DefaultDivesPerLunation;

    public const int DefaultDivesPerLunation = 3;

    [JsonIgnore]
    public bool CanMoveCastle => !CastleMoveSpent;

    [JsonIgnore]
    public bool CanDive => DivesSpent < DivesPerLunation;

    [JsonIgnore]
    public int DivesRemaining => DivesPerLunation - DivesSpent > 0
        ? DivesPerLunation - DivesSpent
        : 0;

    /// <summary>Roll the budget onto a new lunation. Idempotent: calling it
    /// twice for the same lunation does not refund a spent move.</summary>
    public void BeginLunation(int lunation)
    {
        if (lunation == Lunation)
        {
            return;
        }
        Lunation = lunation;
        CastleMoveSpent = false;
        DivesSpent = 0;
    }
}

/// <summary>One force's run, frozen so another force can be driven and this one
/// picked up exactly where it stood.
///
/// <para>Ruled 2026-09-23 (Magos): the scrying table commands EITHER force. One
/// wizard, one table, two forces in different places, and aiming the table is
/// how attention moves between them. That means a run has to survive not being
/// watched, which means it has to leave the ExpeditionManager's fields and
/// become state.</para>
///
/// <para>Modelled on the combat round-trip, which has done exactly this since
/// the beginning: EncounterRouter freezes the run, the scene changes, and
/// RestoreFromCombat thaws it. This is the same seam with a different reason for
/// leaving, so it stores the same things and nothing more. Anything the combat
/// round-trip does NOT need to save is state that lives in the world (fog,
/// discovery, consumed POIs) and is already durable.</para></summary>
public class SortieState
{
    /// <summary>True while this force is IN THE FIELD: deployed, not yet
    /// extracted or parked. The table can only take command of a force whose
    /// sortie is active.</summary>
    public bool Active = false;

    /// <summary>World tile the token stands on.</summary>
    public int X = -1;
    public int Y = -1;

    /// <summary>The staging point this run launched from. The expedition window
    /// is a slice around it, so restoring a run means restoring its origin as
    /// well as its position.</summary>
    public int StagingX = -1;
    public int StagingY = -1;

    public int WindowRadius = 0;

    /// <summary>Fuel for the castle, rations for a party. One counter, two
    /// fictions, exactly as the live run treats it.</summary>
    public int StepsRemaining = 0;
    public int MaxFuel = 0;

    /// <summary>Hull for the castle, Health for a party.</summary>
    public int CurrentHP = 0;
    public int MaxHP = 0;

    public int GoldEarned = 0;
    public int SplinterEarned = 0;
    public int MaterialEarned = 0;
    public int SuppliesEarned = 0;
    public int EncountersWon = 0;

    /// <summary>Turns of the moon this force has sat in the field unwatched.
    /// Ruled 2026-09-23: a frozen force is not safe, and the longer it sits the
    /// more the locals notice it is not moving. Reset when the sortie ends or is
    /// resumed and driven, so the clock counts neglect, not deployment.</summary>
    public int LunationsFrozen = 0;

    /// <summary>Absolute day the sortie began. The days it costs are counted
    /// from here, not from when it ends, so a run frozen for two moons and then
    /// finished does not charge its walking on top of the moons it sat.</summary>
    public int StartDay = -1;

    public void Clear()
    {
        Active = false;
        LunationsFrozen = 0;
        StartDay = -1;
        X = -1; Y = -1;
        StagingX = -1; StagingY = -1;
        WindowRadius = 0;
        StepsRemaining = 0; MaxFuel = 0;
        CurrentHP = 0; MaxHP = 0;
        GoldEarned = 0; SplinterEarned = 0; MaterialEarned = 0; SuppliesEarned = 0;
        EncountersWon = 0;
    }

    /// <summary>Active AND holding a run that could be resumed. A method, not a
    /// property, so the JSON writer leaves it alone. Active alone was trusted
    /// until 2026-09-23, when saves made under the slot-ordering bug turned out
    /// to hold Active slots at a real position with 0/0 vitals; every reader
    /// of Active (the strategic roster, the frozen-sortie roll, the expedition
    /// fork) has to agree on what a live run is, and this is where they agree.</summary>
    public bool IsLive() => Active && X >= 0 && Y >= 0 && MaxFuel > 0 && CurrentHP > 0;

    public override string ToString() =>
        Active ? $"at ({X},{Y}) {StepsRemaining}/{MaxFuel} {CurrentHP}/{MaxHP}" : "not in the field";
}

/// <summary>How a message reaches the wizard at the stone. Ruled 2026-09-23
/// (Magos): the player looks up from the palantir, and news arrives by one of
/// three roads, each with its own weight.</summary>
public enum ScryChannel
{
    /// <summary>A note left on the desk beside the stone. Waits to be read.
    /// Nothing interrupts; the wizard looks up when they choose to.</summary>
    Note = 0,

    /// <summary>Someone comes into the chamber with something to say. Announces
    /// itself, waits to be dismissed, does not stop the scrying.</summary>
    Messenger = 1,

    /// <summary>Mind to mind. Arrives THROUGH the stone, which clouds for a
    /// moment; the scrying pauses until the sending has been received.</summary>
    Sending = 2,
}

/// <summary>One message, whichever road it took. Kept on the cycle so an
/// unread note survives aiming the table elsewhere, a save, and a crash: a note
/// on a desk does not vanish because the wizard looked away.</summary>
/// <summary>The one way anything writes to the desk. Kept beside the message
/// type so every producer (frozen-sortie rolls, field work, later the zones)
/// stamps ids and lunations the same way.</summary>
public static class ScryInbox
{
    public static void Post(CycleState cycle, ScryChannel channel, string title, string body,
                            string forceId, string idPrefix)
    {
        if (cycle == null)
        {
            return;
        }
        cycle.ScryInbox ??= new List<ScryMessage>();
        cycle.ScryInbox.Add(new ScryMessage
        {
            Id = $"{idPrefix}_{cycle.Calendar?.CurrentLunation ?? 0}_{cycle.ScryInbox.Count}",
            Channel = channel,
            Title = title,
            Body = body,
            Lunation = cycle.Calendar?.CurrentLunation ?? 0,
            Read = false,
            AboutForceId = forceId ?? "",
        });
    }
}

public class ScryMessage
{
    public string Id = "";
    public ScryChannel Channel = ScryChannel.Note;
    public string Title = "";
    public string Body = "";

    /// <summary>Lunation it arrived, for the desk's ordering and the log.</summary>
    public int Lunation = 0;

    public bool Read = false;

    /// <summary>Which force the message concerns, or empty for the guild at
    /// large. "" for the castle, a FieldParty.Id otherwise. Lets the desk say
    /// whose news this is when the wizard is watching somebody else.</summary>
    public string AboutForceId = "";
}

/// <summary>What the fortress is carrying but has not banked. A castle that
/// parks in the field never reaches the dock, so its spoils cannot bank the way
/// a recall banks them. They ride in the hold instead, accumulating across
/// parks, until something brings them home.
///
/// <para>Ruled 2026-09-21: a field party carries the hold back. Until field
/// party movement exists, a voluntary recall also banks it, so nothing can be
/// stranded permanently.</para></summary>
public class CastleHold
{
    public int Gold = 0;
    public int Splinters = 0;
    public int Materials = 0;
    public int Supplies = 0;

    [JsonIgnore]
    public bool IsEmpty => Gold == 0 && Splinters == 0 && Materials == 0 && Supplies == 0;

    public void Add(int gold, int splinters, int materials, int supplies)
    {
        Gold += gold;
        Splinters += splinters;
        Materials += materials;
        Supplies += supplies;
    }

    public void Clear()
    {
        Gold = 0;
        Splinters = 0;
        Materials = 0;
        Supplies = 0;
    }

    public override string ToString() =>
        $"{Gold}g {Splinters}sp {Materials}mat {Supplies}sup";
}

/// <summary>Which force a run drives. Lives beside the rest of the split-forces
/// data rather than on PlayerSession, because the distinction is a design fact
/// about the game and the handoff static is only one of its readers.</summary>
public enum ExpeditionRunKind
{
    Castle = 0,
    Field = 1,
}
