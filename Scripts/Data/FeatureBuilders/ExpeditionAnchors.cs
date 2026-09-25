using Godot;
using System.Collections.Generic;

// ============================================================
// ExpeditionAnchors.cs
//
// Purpose:        The one place that answers "where can the field party
//                 go?". Ruling 2026-09-21 (Magos): the castle parks and
//                 becomes a waypoint, and the field party travels to
//                 KNOWN waypoints: cities, shard zones, built waypoints.
//
//                 Rather than inventing a second destination list beside
//                 the 29 existing StagingPoints call sites, this builds a
//                 UNION VIEW over what the world already tracks:
//                   StagingPoints (start, secured outposts, settlements,
//                     seats; a city grants staging through a POI at its
//                     centre, so cities arrive here for free)
//                   ShardZones that have been discovered
//                   the parked castle, if it is on the map
//                   built waypoints with charges left
//                 Nothing about StagingPoint's existing meaning changes;
//                 AnchorRef only classifies it.
//
//                 Also owns the lazy backfill from pre-feature saves:
//                 before the split, ActivePartyCompanionIds WAS the castle
//                 crew, so those companions become Posting.Crew on first
//                 touch and no migration or version bump is needed.
//
//                 Read-mostly and non-throwing: a malformed or partial
//                 save degrades to a shorter anchor list rather than
//                 breaking the strategic view.
// Layer:          Data (feature builder, no nodes of its own)
// Collaborators:  CycleState.cs, WorldData.cs (StagingPoint, ShardZone),
//                 FieldExpeditionState.cs (Waypoint, FieldParty,
//                 CompanionPosting), CompanionDefinition.cs
// See:            docs/expedition_bastion_and_field_party_handoff_v1.md,
//                 docs/expedition_bastion_pressure_test_2026-09-21.md
// ============================================================

/// <summary>One destination in the unified anchor list. A runtime view over
/// the world tables, never serialized: rebuild it, do not store it.</summary>
public sealed class AnchorRef
{
    public AnchorKind Kind;
    public int X;
    public int Y;
    public string Name = "";

    /// <summary>Stable identity, "kind:x,y". FieldParty.AnchorKey holds this.</summary>
    public string Key = "";

    /// <summary>Dives left for a built waypoint, or -1 for a permanent anchor
    /// that never runs out.</summary>
    public int ChargesLeft = -1;

    /// <summary>False when the anchor is known but cannot be used right now
    /// (an unavailable staging point, a spent waypoint).</summary>
    public bool Available = true;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsPermanent => ChargesLeft < 0;
}

/// <summary>Builds the field party's destination list and keeps postings
/// consistent. Every method tolerates nulls and partial state.</summary>
public static class ExpeditionAnchors
{
    /// <summary>Id of the party that exists before any campus upgrade grants
    /// more. The turn loop iterates the list regardless, so extra parties are
    /// a data change and never a refactor.</summary>
    public const string PrimaryFieldPartyId = "field_1";

    /// <summary>StagingPoint.Source marking the one staging point that IS the
    /// parked fortress. Exactly one can exist at a time: parking elsewhere moves
    /// it, because the castle is in one place.</summary>
    public const string CastleStagingSource = "Castle";

    /// <summary>StagingPoint.Source marking a BUILT waypoint's staging point.
    ///
    /// <para>Found on review, 2026-09-21: waypoints were written into
    /// cycle.Waypoints and surfaced in All(), and nothing else in the game reads
    /// All(). The deploy drawer, the strategic markers and the supply-anchor
    /// rules all read StagingPoints, so a conjured waypoint was a row in the save
    /// that the player could not click, travel to, or dive from. Raising one did
    /// nothing at all.</para>
    ///
    /// <para>The fix is the same trick ParkCastleAt already uses: give it a real
    /// StagingPoint and it inherits the whole shipped machinery. Unlike the
    /// castle's, several can exist at once, and each is removed when its last
    /// charge is spent.</para></summary>
    public const string WaypointStagingSource = "Waypoint";

    /// <summary>Stand-in tank capacity before the first sortie has measured the
    /// real one. Mirrors ExpeditionManager.OperatingRange's default.</summary>
    public const int ProvisionalMaxFuel = 40;

    // ── The union view ───────────────────────────────────────────────────

    /// <summary>Every anchor the field party could travel to, in a stable
    /// order (castle first, then staging, shard zones, built waypoints).
    /// Includes unavailable ones so the UI can grey rather than hide them;
    /// filter on AnchorRef.Available to get only the usable ones.</summary>
    public static List<AnchorRef> All(CycleState cycle)
    {
        var list = new List<AnchorRef>();
        if (cycle == null)
        {
            return list;
        }

        // The parked castle. A mobile anchor, and the reason parking is a
        // strategic decision. CastleX/Y tracks the fortress at all times, but it
        // is only an ANCHOR once parked: a castle mid-sortie is somewhere, not
        // somewhere you can launch from.
        bool castleOnExistingStaging = false;
        if (cycle.World?.StagingPoints != null)
        {
            foreach (var sp in cycle.World.StagingPoints)
            {
                if (sp != null && sp.Source != CastleStagingSource
                    && sp.X == cycle.CastleX && sp.Y == cycle.CastleY)
                {
                    castleOnExistingStaging = true;
                    break;
                }
            }
        }

        if (cycle.CastleParked && cycle.CastleX >= 0 && cycle.CastleY >= 0
            && !castleOnExistingStaging)
        {
            list.Add(new AnchorRef
            {
                Kind = AnchorKind.CastlePark,
                X = cycle.CastleX,
                Y = cycle.CastleY,
                Name = "The castle",
                Key = CastleKeyOf(cycle.CastleX, cycle.CastleY),
                ChargesLeft = -1,
                Available = true,
            });
        }

        var world = cycle.World;
        if (world == null)
        {
            return list;
        }

        // Staging points. Cities and secured outposts arrive through here,
        // because granting staging is already how the world marks them.
        if (world.StagingPoints != null)
        {
            foreach (var sp in world.StagingPoints)
            {
                if (sp == null)
                {
                    continue;
                }

                // The parked castle already appears above as AnchorKind.CastlePark.
                // Its StagingPoint exists so the shipped deploy drawer, markers and
                // supply-anchor rules pick it up for free, not so it can be listed
                // twice.
                if (sp.Source == CastleStagingSource)
                {
                    continue;
                }

                // Likewise a built waypoint: it owns a StagingPoint so the
                // shipped UI can reach it, and it is listed below as
                // AnchorKind.Built with its charge count. Listing it here too
                // would put it on the field party's destination list twice.
                if (sp.Source == WaypointStagingSource)
                {
                    continue;
                }

                list.Add(new AnchorRef
                {
                    Kind = AnchorKind.Staging,
                    X = sp.X,
                    Y = sp.Y,
                    Name = string.IsNullOrEmpty(sp.Name) ? "Staging point" : sp.Name,
                    Key = StagingKeyOf(sp.X, sp.Y),
                    ChargesLeft = -1,
                    Available = sp.Available,
                });
            }
        }

        // Shard zones, once discovered. The zone centre is the anchor; the
        // gate and sanctum are dived from it.
        if (world.ShardZones != null)
        {
            foreach (var z in world.ShardZones)
            {
                if (z == null || !z.Discovered)
                {
                    continue;
                }
                list.Add(new AnchorRef
                {
                    Kind = AnchorKind.ShardZone,
                    X = z.CenterX,
                    Y = z.CenterY,
                    Name = string.IsNullOrEmpty(z.Name) ? "Shard zone" : z.Name,
                    Key = ShardKeyOf(z.CenterX, z.CenterY),
                    ChargesLeft = -1,
                    Available = !z.ShardCollected,
                });
            }
        }

        // Built waypoints, the consumable kind the castle conjures.
        if (cycle.Waypoints != null)
        {
            foreach (var w in cycle.Waypoints)
            {
                if (w == null)
                {
                    continue;
                }
                var poi = world.PoiAt(w.X, w.Y);
                list.Add(new AnchorRef
                {
                    Kind = AnchorKind.Built,
                    X = w.X,
                    Y = w.Y,
                    Name = poi != null ? $"Waypoint ({poi.Kind})" : "Waypoint",
                    Key = string.IsNullOrEmpty(w.Key) ? Waypoint.KeyOf(w.X, w.Y) : w.Key,
                    ChargesLeft = w.Charges,
                    Available = !w.IsSpent,
                });
            }
        }

        return list;
    }

    /// <summary>The anchor standing on this tile, or null. Prefer this to Find
    /// when what you have is a POSITION: one tile can carry two anchor rows (the
    /// parked castle and the staging point underneath it) with different keys,
    /// and a key lookup will miss the one you did not guess.</summary>
    public static AnchorRef FindAt(CycleState cycle, int x, int y)
    {
        foreach (var a in All(cycle))
        {
            if (a.X == x && a.Y == y)
            {
                return a;
            }
        }
        return null;
    }

    /// <summary>The anchor with the given key, or null. Rebuilds the list, so
    /// cache the result if calling in a loop.</summary>
    public static AnchorRef Find(CycleState cycle, string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }
        foreach (var a in All(cycle))
        {
            if (a.Key == key)
            {
                return a;
            }
        }
        return null;
    }

    // ── Keys (one place builds each, so nothing drifts) ──────────────────

    public static string CastleKeyOf(int x, int y) => $"castle:{x},{y}";

    public static string StagingKeyOf(int x, int y) => $"staging:{x},{y}";

    public static string ShardKeyOf(int x, int y) => $"shard:{x},{y}";

    // ── Field party position, travel and the hold ────────────────────────

    /// <summary>Put the field party on the map. Idempotent, safe on every load.
    /// Without it the party sits at (-1,-1) and every "are you here?" test
    /// against a staging point is false, so no dive is ever offered and the
    /// force stays theoretical. They start where the castle is, which is the
    /// dock at the top of a cycle.</summary>
    public static void EnsureFieldPartySited(CycleState cycle)
    {
        var party = EnsurePrimaryParty(cycle);
        if (party == null || cycle?.World == null)
        {
            return;
        }
        EnsureFieldParties(cycle);
        if (!cycle.World.InBounds(cycle.CastleX, cycle.CastleY))
        {
            return;   // the castle is not sited yet either; next load
        }
        // Every PARTY (never a detachment: those are sited where they were
        // posted) that has not yet taken the field starts with the castle.
        foreach (var p in cycle.FieldParties)
        {
            if (p == null || p.X >= 0 || !string.IsNullOrEmpty(p.ParentId))
            {
                continue;
            }
            p.X = cycle.CastleX;
            p.Y = cycle.CastleY;
            p.AnchorKey = CastleKeyOf(cycle.CastleX, cycle.CastleY);
            p.State = FieldPartyState.AtAnchor;
            GD.Print($"[ExpeditionAnchors] {p.Name} sited with the castle at ({p.X},{p.Y}).");
        }
    }

    /// <summary>Field Party v1 (2026-09-24): parties exist up to
    /// CycleState.MaxFieldParties, which the Grand Hall raises. Never removes
    /// one: a party the cap no longer covers keeps its people and its place,
    /// and the cap only stops new ones being made.</summary>
    public static void EnsureFieldParties(CycleState cycle)
    {
        if (cycle?.FieldParties == null)
        {
            return;
        }
        int have = 0;
        foreach (var p in cycle.FieldParties)
        {
            if (p != null && string.IsNullOrEmpty(p.ParentId))
            {
                have++;
            }
        }
        int want = Mathf.Max(1, cycle.MaxFieldParties);
        for (int n = have + 1; n <= want; n++)
        {
            string id = $"field_{n}";
            if (cycle.FieldParties.Exists(p => p != null && p.Id == id))
            {
                continue;
            }
            cycle.FieldParties.Add(new FieldParty
            {
                Id = id,
                Name = n == 2 ? "Second Party" : n == 3 ? "Third Party" : $"Party {n}",
            });
            GD.Print($"[ExpeditionAnchors] Raised {id}: MaxFieldParties is {want}.");
        }
    }

    // ── Built waypoints ──────────────────────────────────────────────────

    /// <summary>Charges a newly conjured waypoint carries. One by default, plus
    /// any installed module with the "waypoint_charges" effect. No module grants
    /// it yet, and that is fine: the hook is what makes waypoint depth an upgrade
    /// rather than a constant, and CastleModules is the upgrade ladder that
    /// actually exists.</summary>
    public static int WaypointChargesFromModules()
    {
        int charges = 1;
        foreach (var id in CastleModules.InstalledIds())
        {
            var d = CastleModules.Get(id);
            if (d != null && d.Effect == "waypoint_charges")
            {
                charges += d.Magnitude;
            }
        }
        return charges < 1 ? 1 : charges;
    }

    /// <summary>Can the castle raise a waypoint on this tile, and if not, why?
    /// The refusal is a sentence rather than a bool so the control can wear it as
    /// a tooltip and teach the rule before the click instead of after it.</summary>
    public static bool CanConjureWaypoint(CycleState cycle, int x, int y, out string why)
    {
        why = null;
        if (cycle?.World == null || !cycle.World.InBounds(x, y))
        {
            return false;
        }

        var poi = cycle.World.PoiAt(x, y);
        if (poi == null)
        {
            why = "A waypoint needs something to anchor to. Stand on a discovered site.";
            return false;
        }
        if (!poi.Discovered)
        {
            why = "The site is not charted yet.";
            return false;
        }

        if (cycle.Waypoints != null)
        {
            foreach (var w in cycle.Waypoints)
            {
                if (w != null && w.X == x && w.Y == y && !w.IsSpent)
                {
                    why = "A waypoint already stands here.";
                    return false;
                }
            }
        }

        if (cycle.World.StagingPoints != null)
        {
            foreach (var sp in cycle.World.StagingPoints)
            {
                if (sp != null && sp.X == x && sp.Y == y && sp.Source != CastleStagingSource)
                {
                    why = "This ground already stages an expedition. A waypoint would add nothing.";
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>Raise a built waypoint over a discovered POI. The consumable kind
    /// of anchor: it carries charges, it appears in All() as AnchorKind.Built,
    /// and a field party can travel to it like any other destination.
    ///
    /// <para>Returns a line for the log, or null if refused. The caller checks
    /// CanConjureWaypoint first and shows the refusal; this re-checks anyway,
    /// because a control that was enabled a moment ago is not a guarantee.</para></summary>
    public static string ConjureWaypoint(CycleState cycle, int x, int y)
    {
        if (!CanConjureWaypoint(cycle, x, y, out _))
        {
            return null;
        }

        cycle.Waypoints ??= new List<Waypoint>();
        int charges = WaypointChargesFromModules();
        var poi = cycle.World.PoiAt(x, y);

        cycle.Waypoints.Add(new Waypoint
        {
            Key = Waypoint.KeyOf(x, y),
            PoiIndex = cycle.World.TryIndex(x, y, out int idx) ? cycle.World.Tiles[idx].PoiIndex : -1,
            X = x,
            Y = y,
            Charges = charges,
            CreatedLunation = cycle.Calendar?.CurrentLunation ?? 0,
            CreatedBy = "castle",
        });

        // A real StagingPoint, so the deploy drawer, the map markers and the
        // supply-anchor rules pick it up without a parallel system.
        cycle.World.StagingPoints ??= new List<StagingPoint>();
        string what = poi != null ? poi.Kind.ToString() : "the site";
        cycle.World.StagingPoints.Add(new StagingPoint
        {
            X = x,
            Y = y,
            Name = $"Waystone ({what})",
            Source = WaypointStagingSource,
            Available = true,
        });

        string line = $"A waystone rises over {what} at ({x},{y}): {charges} dive(s) before it closes.";
        GD.Print($"[ExpeditionAnchors] {line}");
        return line;
    }

    /// <summary>Close every spent waystone, removing the StagingPoint that let
    /// the player click it. Called on strategic load, NOT at the moment the last
    /// charge is spent.
    ///
    /// <para>The timing is the whole point, and the first cut got it wrong.
    /// A dive spends the charge at DEPLOY, so closing the door there would have
    /// deleted the staging point the party is standing on and then sent them
    /// out: OnSupplyAnchor would have found nothing on their own tile, turning
    /// every extraction from a last-charge waystone into an emergency one, with
    /// the straggle lunation and the infirmary check that come with it. The
    /// waystone holds until they are home.</para></summary>
    public static void PruneSpentWaypoints(CycleState cycle)
    {
        if (cycle?.Waypoints == null)
        {
            return;
        }
        foreach (var w in cycle.Waypoints)
        {
            if (w == null || !w.IsSpent)
            {
                continue;
            }
            int removed = cycle.World?.StagingPoints?.RemoveAll(
                sp => sp != null && sp.Source == WaypointStagingSource
                      && sp.X == w.X && sp.Y == w.Y) ?? 0;
            if (removed > 0)
            {
                GD.Print($"[ExpeditionAnchors] The waystone at ({w.X},{w.Y}) closes.");
            }
        }
        cycle.Waypoints.RemoveAll(w => w != null && w.IsSpent);
    }

    /// <summary>Clear any sortie slot that is Active but not live (see
    /// SortieState.IsLive). Such slots exist only in saves made under the
    /// slot-ordering bug of 2026-09-23; nothing on the fixed build produces
    /// one. Run on the way into the strategic map, the same moment the spent
    /// waystones close.</summary>
    public static void PruneDeadSorties(CycleState cycle)
    {
        if (cycle == null)
        {
            return;
        }
        if (cycle.CastleSortie != null && cycle.CastleSortie.Active && !cycle.CastleSortie.IsLive())
        {
            GD.PrintErr($"[ExpeditionAnchors] Castle sortie slot was Active but dead ({cycle.CastleSortie}); cleared.");
            cycle.CastleSortie.Clear();
        }
        if (cycle.FieldParties != null)
        {
            foreach (var p in cycle.FieldParties)
            {
                if (p?.Sortie == null || !p.Sortie.Active || p.Sortie.IsLive())
                {
                    continue;
                }
                GD.PrintErr($"[ExpeditionAnchors] {p.Name} sortie slot was Active but dead ({p.Sortie}); cleared.");
                p.Sortie.Clear();
            }
        }
    }

    /// <summary>The resupply bill for a castle making camp: one lunation when
    /// pristine, up to the cap when wrecked. ONE formula for the scene's
    /// ParkCastle and the strategic Bank the furnace, so the two ways of making
    /// camp cannot quote different bills.</summary>
    public static int RepairLunationsFor(int hull, int maxHull, int minRepair, int maxRepair)
    {
        int missing = Mathf.Max(0, maxHull - hull);
        int repair = maxHull > 0
            ? 1 + Mathf.FloorToInt(4f * missing / maxHull)
            : minRepair;
        return Mathf.Clamp(repair, minRepair, maxRepair);
    }

    /// <summary>Make camp with a FROZEN castle, from the strategic map, without
    /// entering the scene (2026-09-23: until now the only ways out of a sortie
    /// were walking home or running the tank dry, so the free parked state that
    /// the field parties need as an anchor could only be reached by burning
    /// the fuel first). Same bookkeeping as the scene's ParkCastle: tank to the
    /// cycle, un-banked earnings to the hold, a waypoint on the tile, and the
    /// slot cleared. Returns the report line, or null when there was nothing
    /// to bank.</summary>
    public static string BankFrozenCastle(CycleState cycle, string castleName, int minRepair, int maxRepair)
    {
        var slot = cycle?.CastleSortie;
        if (slot == null || !slot.IsLive())
        {
            return null;
        }
        int repair = RepairLunationsFor(slot.CurrentHP, slot.MaxHP, minRepair, maxRepair);
        int x = slot.X, y = slot.Y;
        cycle.CastleFuel = Mathf.Max(0, slot.StepsRemaining);
        cycle.CastleMaxFuel = slot.MaxFuel;
        cycle.CastleHold ??= new CastleHold();
        cycle.CastleHold.Add(slot.GoldEarned, slot.SplinterEarned, slot.MaterialEarned, slot.SuppliesEarned);
        ParkCastleAt(cycle, x, y, castleName, repair);
        slot.Clear();
        GD.Print($"[ExpeditionAnchors] {castleName} banks the furnace at ({x},{y}); {repair} lunation(s) of resupply.");
        return $"{castleName} banks the furnace at ({x},{y}) and makes camp. This ground is a waypoint now. "
             + $"Work crews teleport in to refuel, restock and repair: {repair} lunation(s), and the castle is "
             + "exposed for every one of them.";
    }

    /// <summary>Spend one charge of the built waypoint on this tile, if there is
    /// one. Called when a field party dives from it, which is the only thing that
    /// consumes a waypoint. Returns true if a charge was spent.</summary>
    public static bool SpendWaypointCharge(CycleState cycle, int x, int y)
    {
        if (cycle?.Waypoints == null)
        {
            return false;
        }
        foreach (var w in cycle.Waypoints)
        {
            if (w != null && w.X == x && w.Y == y && !w.IsSpent)
            {
                w.Charges--;
                GD.Print($"[ExpeditionAnchors] Waypoint at ({x},{y}) spends a charge; {w.Charges} left.");
                return true;
            }
        }
        return false;
    }

    // ── Postings: backfill and reconcile ─────────────────────────────────

    /// <summary>Lazy migration for saves written before the force split.
    /// Before the split, ActivePartyCompanionIds WAS the castle crew, so those
    /// companions become Posting.Crew and everyone else stays Campus. Runs at
    /// most once per save: it no-ops as soon as any companion carries a
    /// non-Campus posting. Safe to call on every strategic-map load.</summary>
    public static void BackfillPostings(CycleState cycle)
    {
        if (cycle == null || cycle.Companions == null)
        {
            return;
        }

        foreach (var c in cycle.Companions)
        {
            if (c != null && c.Posting != CompanionPosting.Campus)
            {
                EnsurePrimaryParty(cycle);
                return;
            }
        }

        var active = cycle.ActivePartyCompanionIds;
        if (active != null && active.Count > 0)
        {
            foreach (var c in cycle.Companions)
            {
                if (c != null && active.Contains(c.Id))
                {
                    c.Posting = CompanionPosting.Crew;
                    c.FieldPartyId = "";
                }
            }
            GD.Print($"[ExpeditionAnchors] Backfilled {active.Count} companion(s) to the castle crew.");
        }

        EnsurePrimaryParty(cycle);
    }

    /// <summary>Make sure the one baseline field party exists. Extra parties
    /// are a campus upgrade later; the list is iterated from day one.</summary>
    public static FieldParty EnsurePrimaryParty(CycleState cycle)
    {
        if (cycle == null)
        {
            return null;
        }
        cycle.FieldParties ??= new List<FieldParty>();

        foreach (var p in cycle.FieldParties)
        {
            if (p != null && p.Id == PrimaryFieldPartyId)
            {
                return p;
            }
        }

        var party = new FieldParty
        {
            Id = PrimaryFieldPartyId,
            Name = "Field Party",
        };
        cycle.FieldParties.Add(party);
        return party;
    }

    /// <summary>Rebuild every party's member list from the companions' own
    /// postings. Companion.Posting plus Companion.FieldPartyId is the single
    /// source of truth; FieldParty.MemberCompanionIds is the ordered view.
    /// Call after any posting change, and on load, so the two cannot drift.</summary>
    public static void ReconcilePostings(CycleState cycle)
    {
        if (cycle == null || cycle.FieldParties == null)
        {
            return;
        }

        foreach (var p in cycle.FieldParties)
        {
            if (p != null)
            {
                p.MemberCompanionIds ??= new List<string>();
                p.MemberCompanionIds.Clear();
            }
        }

        if (cycle.Companions == null)
        {
            return;
        }

        foreach (var c in cycle.Companions)
        {
            if (c == null || c.Posting != CompanionPosting.Field)
            {
                continue;
            }

            string partyId = string.IsNullOrEmpty(c.FieldPartyId)
                ? PrimaryFieldPartyId
                : c.FieldPartyId;

            FieldParty target = null;
            foreach (var p in cycle.FieldParties)
            {
                if (p != null && p.Id == partyId)
                {
                    target = p;
                    break;
                }
            }

            // A posting that names a party which no longer exists falls back
            // to the primary one rather than silently dropping the companion.
            target ??= EnsurePrimaryParty(cycle);
            if (target != null)
            {
                c.FieldPartyId = target.Id;
                target.MemberCompanionIds.Add(c.Id);
            }
        }
    }


    // ── Parking ──────────────────────────────────────────────────────────

    /// <summary>Park the fortress where it stands. Ruled 2026-09-21: running the
    /// furnace dry parks the castle rather than stranding it, the parked castle
    /// becomes a waypoint, and it refuels on the next sortie.
    ///
    /// <para>The waypoint is a real StagingPoint carrying Source "Castle", which
    /// is the whole trick: the shipped deploy drawer, the strategic markers, and
    /// the supply-leash anchor rules all read StagingPoints, so parking earns all
    /// three without a parallel system. Exactly one castle staging point exists at
    /// a time, because the castle is in one place; parking again moves it.</para>
    ///
    /// <para>Also points LastDeployStagingKey at the new spot, so the next deploy
    /// opens there and the castle carries on from where it stopped instead of
    /// blinking back to the dock.</para></summary>
    public static void ParkCastleAt(CycleState cycle, int x, int y, string castleName,
                                    int repairLunations)
    {
        if (cycle?.World == null || !cycle.World.InBounds(x, y))
        {
            return;
        }
        // The day clock (2026-09-23): the resupply counts its days from the
        // camp, not from the next new moon.
        cycle.CastleRepairDayAccum = 0;

        cycle.CastleX = x;
        cycle.CastleY = y;
        cycle.CastleParked = true;
        cycle.CastleParkedLunation = cycle.Calendar?.CurrentLunation ?? 0;
        cycle.CastleRepairLunations = repairLunations < 0 ? 0 : repairLunations;

        cycle.World.StagingPoints ??= new List<StagingPoint>();
        cycle.World.StagingPoints.RemoveAll(sp => sp != null && sp.Source == CastleStagingSource);

        string name = string.IsNullOrEmpty(castleName) ? "The castle" : castleName;

        // A staging point may already sit on this tile (an outpost the castle
        // parked on top of). Leave it alone and let it serve: two launch points
        // on one tile would render and list twice for no gain.
        bool tileAlreadyStages = false;
        foreach (var sp in cycle.World.StagingPoints)
        {
            if (sp != null && sp.X == x && sp.Y == y)
            {
                tileAlreadyStages = true;
                break;
            }
        }

        if (!tileAlreadyStages)
        {
            cycle.World.StagingPoints.Add(new StagingPoint
            {
                X = x,
                Y = y,
                Name = name,
                Source = CastleStagingSource,
                // Unavailable while the crews work: a castle mid-resupply cannot
                // launch a sortie, which is what makes the repair bill bite.
                Available = cycle.CastleRepairLunations <= 0,
            });
        }

        cycle.LastDeployStagingKey = $"{x},{y}";
        GD.Print($"[ExpeditionAnchors] {name} parks at ({x},{y}) on lunation {cycle.CastleParkedLunation}, "
                 + $"resupply {cycle.CastleRepairLunations} lunation(s).");
    }

    /// <summary>Advance the resupply by one lunation. Called from the lunation
    /// tick, so it runs on the same clock as corruption and kingdom drift.
    ///
    /// <para>When the last lunation is paid the work is done: the crews have
    /// refuelled and restocked the castle, and they carry the hold home with
    /// them. That teleport IS how treasure reaches the guild, so the hold banks
    /// here rather than waiting for the castle to dock.</para>
    ///
    /// <para>Returns true on the lunation the resupply completes, so the caller
    /// can announce it.</para></summary>
    public static bool TickCastleRepair(CycleState cycle)
    {
        if (cycle == null || !cycle.CastleParked || cycle.CastleRepairLunations <= 0)
        {
            return false;
        }

        cycle.CastleRepairLunations--;
        if (cycle.CastleRepairLunations > 0)
        {
            return false;
        }

        // The crews go home, and the hold goes with them.
        var hold = cycle.CastleHold;
        if (hold != null && !hold.IsEmpty)
        {
            cycle.Gold += hold.Gold;
            cycle.ArcaneSplinters += hold.Splinters;
            cycle.BuildMaterials += hold.Materials;
            cycle.Supplies += hold.Supplies;
            GD.Print($"[ExpeditionAnchors] The waystone carries the hold home: {hold}.");
            hold.Clear();
        }

        // Section 3.2: the crews refuel the furnace as part of the resupply, so
        // the tank is full the moment the work is done. That is what makes the
        // repair clock the ONLY gate on the next move.
        if (cycle.CastleMaxFuel > 0)
        {
            cycle.CastleFuel = cycle.CastleMaxFuel;
        }

        // The castle can sortie again from where it stands.
        if (cycle.World?.StagingPoints != null)
        {
            foreach (var sp in cycle.World.StagingPoints)
            {
                if (sp != null && sp.Source == CastleStagingSource)
                {
                    sp.Available = true;
                }
            }
        }
        return true;
    }

    /// <summary>Bring the fortress to dock. It is still PARKED, because parked
    /// means "stationary on the world and able to act" rather than "camped in the
    /// field": a docked castle can still march, and the first version of this
    /// cleared the flag, which meant the March control never appeared until the
    /// player had run a tank dry. What ends is the field CAMP, so the castle's
    /// own staging point is retired (the dock has its own) and the resupply is
    /// settled by the turnaround. Staging points earned another way, such as an
    /// outpost the castle happened to park on, are untouched.</summary>
    public static void UnparkCastle(CycleState cycle)
    {
        if (cycle == null)
        {
            return;
        }
        cycle.CastleParked = true;
        cycle.CastleRepairLunations = 0;
        if (cycle.CastleMaxFuel > 0)
        {
            cycle.CastleFuel = cycle.CastleMaxFuel;   // the dock turnaround refuels
        }
        cycle.World?.StagingPoints?.RemoveAll(sp => sp != null && sp.Source == CastleStagingSource);
    }

    /// <summary>Put the castle on the map at the start of a cycle. Idempotent and
    /// safe to call on every strategic load: it only acts when the fortress has no
    /// position yet. Without it a fresh cycle has CastleX/Y at -1, so no anchor,
    /// no march and no readout until the player has parked once.</summary>
    public static void EnsureCastleSited(CycleState cycle)
    {
        if (cycle?.World == null || cycle.CastleX >= 0)
        {
            return;
        }

        int x = cycle.World.HomeX, y = cycle.World.HomeY;
        if (!cycle.World.InBounds(x, y) && cycle.World.StagingPoints != null)
        {
            foreach (var sp in cycle.World.StagingPoints)
            {
                if (sp != null && sp.Source == "Start")
                {
                    x = sp.X;
                    y = sp.Y;
                    break;
                }
            }
        }
        if (!cycle.World.InBounds(x, y))
        {
            return;
        }

        cycle.CastleX = x;
        cycle.CastleY = y;
        cycle.CastleParked = true;
        cycle.CastleRepairLunations = 0;
        if (cycle.CastleMaxFuel <= 0)
        {
            // A provisional tank so the march reads sensibly before the first
            // sortie. ExpeditionManager writes the REAL capacity (chassis, crew,
            // modules, campus) the first time the castle parks or docks.
            cycle.CastleMaxFuel = ProvisionalMaxFuel;
            cycle.CastleFuel = ProvisionalMaxFuel;
        }
        GD.Print($"[ExpeditionAnchors] Castle sited at the dock ({x},{y}).");
    }

    /// <summary>Companions currently posted to the castle crew, in roster
    /// order. The crew is what staffs CrewStations and drives fortress
    /// characteristics, so this is the list the fortress systems read.</summary>
    public static List<Companion> Crew(CycleState cycle)
    {
        var crew = new List<Companion>();
        if (cycle == null || cycle.Companions == null)
        {
            return crew;
        }
        foreach (var c in cycle.Companions)
        {
            if (c != null && c.Posting == CompanionPosting.Crew && !c.IsInjured)
            {
                crew.Add(c);
            }
        }
        return crew;
    }
}
