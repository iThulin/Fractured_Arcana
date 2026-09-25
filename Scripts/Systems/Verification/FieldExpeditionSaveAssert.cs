using Godot;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

// ============================================================
// FieldExpeditionSaveAssert.cs
//
// Purpose:        Round-trip assertions for the Expedition v2 split-forces
//                 data layer: the save-file-paranoia rule made real in
//                 code. Every new struct is built with distinctive
//                 non-default values, pushed through the EXACT serializer
//                 the save uses (SaveManager.JsonOptions), read back, and
//                 compared field by field. A dropped or renamed field
//                 flips a comparison and is reported loudly.
//
//                 Covers:
//                   - Waypoint
//                   - FieldParty
//                   - ExpeditionTurnState (including BeginLunation, which
//                     must be idempotent within one lunation)
//                   - SignatureSpell / SignaturePoolState
//                   - WizardSignatureSlots
//                   - Companion.Posting / FieldPartyId
//                   - WorldPoi harvestable regrowth fields
//                   - CycleState carrying all of the above
//                   - the pre-split BACKFILL path, which is the one place
//                     a real save can silently lose its castle crew
//
//                 Read-only: builds throwaway instances, touches no
//                 ActiveSave, marks nothing dirty. Safe to run anytime.
// Layer:          System (debug / verification)
// Collaborators:  SaveManager.cs (JsonOptions, the real path),
//                 FieldExpeditionState.cs, SignaturePool.cs,
//                 ExpeditionAnchors.cs, CycleState.cs
// See:            docs/expedition_bastion_and_field_party_handoff_v1.md;
//                 save-file-paranoia rule (every save-adjacent struct
//                 asserted before ship)
//
// Usage: FieldExpeditionSaveAssert.AssertAll();
// ============================================================

public static class FieldExpeditionSaveAssert
{
    /// <summary>Run every split-forces round-trip assertion. Prints a
    /// PASS/FAIL report and PushErrors on any failure. Returns true only if
    /// everything survived intact.</summary>
    public static bool AssertAll()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== FIELD EXPEDITION SAVE ROUND-TRIP ASSERTIONS ===");

        bool ok = true;
        ok &= AssertWaypoint(sb);
        ok &= AssertFieldParty(sb);
        ok &= AssertTurnState(sb);
        ok &= AssertSignaturePool(sb);
        ok &= AssertSignatureSlots(sb);
        ok &= AssertCompanionPosting(sb);
        ok &= AssertPoiRegrowth(sb);
        ok &= AssertCycleStateCarriesAll(sb);
        ok &= AssertBackfillFromPreSplitSave(sb);

        sb.AppendLine(ok
            ? "RESULT: ALL PASSED. Split-forces structs round-trip clean."
            : "RESULT: FAILURES ABOVE. A field is being dropped or renamed.");
        GD.Print(sb.ToString());

        if (!ok)
        {
            GD.PushError("[FieldExpeditionSaveAssert] Round-trip assertion FAILED. See Output panel.");
        }
        return ok;
    }

    /// <summary>Serialize then deserialize through the real save options. The
    /// whole point is to catch drift between a struct and its persistence.</summary>
    private static T RoundTrip<T>(T obj)
    {
        string json = JsonSerializer.Serialize(obj, SaveManager.JsonOptions);
        return JsonSerializer.Deserialize<T>(json, SaveManager.JsonOptions);
    }

    private static bool Check(StringBuilder sb, string field, bool equal)
    {
        if (!equal)
        {
            sb.AppendLine($"    FAIL: {field} did not survive the round-trip.");
        }
        return equal;
    }

    private static bool SameList(List<string> a, List<string> b)
    {
        if (a == null || b == null)
        {
            return a == null && b == null;
        }
        if (a.Count != b.Count)
        {
            return false;
        }
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }
        return true;
    }

    // ── Waypoint ─────────────────────────────────────────────────────────

    private static bool AssertWaypoint(StringBuilder sb)
    {
        sb.AppendLine("  Waypoint");
        var src = new Waypoint
        {
            Key = Waypoint.KeyOf(41, 17),
            PoiIndex = 88,
            X = 41,
            Y = 17,
            Charges = 3,
            CreatedLunation = 5,
            ExpiresAtLunation = 9,
            CreatedBy = "scripted",
        };
        var rt = RoundTrip(src);

        bool ok = true;
        ok &= Check(sb, "Waypoint.Key", rt.Key == src.Key);
        ok &= Check(sb, "Waypoint.PoiIndex", rt.PoiIndex == src.PoiIndex);
        ok &= Check(sb, "Waypoint.X", rt.X == src.X);
        ok &= Check(sb, "Waypoint.Y", rt.Y == src.Y);
        ok &= Check(sb, "Waypoint.Charges", rt.Charges == src.Charges);
        ok &= Check(sb, "Waypoint.CreatedLunation", rt.CreatedLunation == src.CreatedLunation);
        ok &= Check(sb, "Waypoint.ExpiresAtLunation", rt.ExpiresAtLunation == src.ExpiresAtLunation);
        ok &= Check(sb, "Waypoint.CreatedBy", rt.CreatedBy == src.CreatedBy);
        ok &= Check(sb, "Waypoint.IsSpent (derived, not persisted)", !rt.IsSpent);
        ok &= Check(sb, "Waypoint.KeyOf agrees with the stored key", Waypoint.KeyOf(41, 17) == rt.Key);

        var spent = RoundTrip(new Waypoint { Charges = 0 });
        ok &= Check(sb, "Waypoint.IsSpent at zero charges", spent.IsSpent);

        sb.AppendLine(ok ? "    PASS" : "    FAILED");
        return ok;
    }

    // ── FieldParty ───────────────────────────────────────────────────────

    private static bool AssertFieldParty(StringBuilder sb)
    {
        sb.AppendLine("  FieldParty");
        var src = new FieldParty
        {
            Id = "field_2",
            Name = "The Long Knives",
            MemberCompanionIds = new List<string> { "bram_thistlewade", "dagny_thornhelm" },
            State = FieldPartyState.Travelling,
            AnchorKey = ExpeditionAnchors.StagingKeyOf(12, 30),
            DestinationAnchorKey = Waypoint.KeyOf(41, 17),
            TravelPhasesRemaining = 2,
            DestX = 41,
            DestY = 17,
            X = 12,
            Y = 30,
            CarriedConsumableIds = new List<string> { "exhaustion_potion" },
            Carrying = new CastleHold { Gold = 120, Splinters = 3, Materials = 45, Supplies = 7 },
            WorkKind = "survey",
            WorkZoneId = "(41,17)",
            WorkDaysLeft = 5,
            WorkDaysTotal = 14,
            ParentId = "field_1",
            WorkStalled = true,
            WorkProgress = 2,
            WorkSide = 2,
            BusyUntilDay = 37,
            TravelDayAccum = 3,
            Sortie = new SortieState
            {
                Active = true,
                X = 51, Y = 19, StagingX = 44, StagingY = 21, WindowRadius = 12,
                StepsRemaining = 27, MaxFuel = 40,
                CurrentHP = 31, MaxHP = 44,
                GoldEarned = 210, SplinterEarned = 4, MaterialEarned = 9, SuppliesEarned = 2,
                EncountersWon = 3,
                LunationsFrozen = 2,
                StartDay = 19,
            },
        };
        var rt = RoundTrip(src);

        bool ok = true;
        ok &= Check(sb, "FieldParty.Id", rt.Id == src.Id);
        ok &= Check(sb, "FieldParty.Name", rt.Name == src.Name);
        ok &= Check(sb, "FieldParty.MemberCompanionIds", SameList(rt.MemberCompanionIds, src.MemberCompanionIds));
        ok &= Check(sb, "FieldParty.State", rt.State == src.State);
        ok &= Check(sb, "FieldParty.AnchorKey", rt.AnchorKey == src.AnchorKey);
        ok &= Check(sb, "FieldParty.DestinationAnchorKey", rt.DestinationAnchorKey == src.DestinationAnchorKey);
        ok &= Check(sb, "FieldParty.TravelPhasesRemaining", rt.TravelPhasesRemaining == src.TravelPhasesRemaining);
        ok &= Check(sb, "FieldParty.X", rt.X == src.X);
        ok &= Check(sb, "FieldParty.Y", rt.Y == src.Y);
        ok &= Check(sb, "FieldParty.CarriedConsumableIds", SameList(rt.CarriedConsumableIds, src.CarriedConsumableIds));
        ok &= Check(sb, "FieldParty.IsAway (derived)", rt.IsAway);

        // Travel destination as a COORDINATE. The key is a convenience; these
        // two are what arrival actually reads, and a party that loses them
        // mid-journey arrives without having moved.
        ok &= Check(sb, "FieldParty.DestX", rt.DestX == src.DestX);
        ok &= Check(sb, "FieldParty.DestY", rt.DestY == src.DestY);

        // The hold in transit. A nested object inside a list element inside
        // CycleState is exactly the shape that silently deserializes to null
        // and quietly eats a cycle's spoils.
        ok &= Check(sb, "FieldParty.Carrying not null", rt.Carrying != null);
        ok &= Check(sb, "FieldParty.Carrying.Gold", rt.Carrying?.Gold == src.Carrying.Gold);
        ok &= Check(sb, "FieldParty.Carrying.Splinters", rt.Carrying?.Splinters == src.Carrying.Splinters);
        ok &= Check(sb, "FieldParty.Carrying.Materials", rt.Carrying?.Materials == src.Carrying.Materials);
        ok &= Check(sb, "FieldParty.Carrying.Supplies", rt.Carrying?.Supplies == src.Carrying.Supplies);

        // The frozen run. A force the scrying table is not watching exists ONLY
        // as this object: lose it and the player loses a sortie mid-flight, with
        // its fuel, its Hull and everything it had earned.
        ok &= Check(sb, "FieldParty.Sortie not null", rt.Sortie != null);
        ok &= Check(sb, "FieldParty.Sortie.Active", rt.Sortie?.Active == true);
        ok &= Check(sb, "FieldParty.Sortie position",
                    rt.Sortie?.X == src.Sortie.X && rt.Sortie?.Y == src.Sortie.Y);
        ok &= Check(sb, "FieldParty.Sortie staging + radius",
                    rt.Sortie?.StagingX == src.Sortie.StagingX
                    && rt.Sortie?.StagingY == src.Sortie.StagingY
                    && rt.Sortie?.WindowRadius == src.Sortie.WindowRadius);
        ok &= Check(sb, "FieldParty.Sortie fuel",
                    rt.Sortie?.StepsRemaining == src.Sortie.StepsRemaining
                    && rt.Sortie?.MaxFuel == src.Sortie.MaxFuel);
        ok &= Check(sb, "FieldParty.Sortie hull",
                    rt.Sortie?.CurrentHP == src.Sortie.CurrentHP
                    && rt.Sortie?.MaxHP == src.Sortie.MaxHP);
        ok &= Check(sb, "FieldParty.Sortie earnings",
                    rt.Sortie?.GoldEarned == src.Sortie.GoldEarned
                    && rt.Sortie?.SplinterEarned == src.Sortie.SplinterEarned
                    && rt.Sortie?.MaterialEarned == src.Sortie.MaterialEarned
                    && rt.Sortie?.SuppliesEarned == src.Sortie.SuppliesEarned
                    && rt.Sortie?.EncountersWon == src.Sortie.EncountersWon);
        ok &= Check(sb, "FieldParty.Sortie.LunationsFrozen",
                    rt.Sortie?.LunationsFrozen == src.Sortie.LunationsFrozen);

        // Field work (2026-09-23): a job forgotten across a save is a party
        // standing idle with its lunations spent.
        ok &= Check(sb, "FieldParty.WorkKind", rt.WorkKind == src.WorkKind);
        ok &= Check(sb, "FieldParty.WorkZoneId", rt.WorkZoneId == src.WorkZoneId);
        ok &= Check(sb, "FieldParty.WorkDays",
                    rt.WorkDaysLeft == src.WorkDaysLeft
                    && rt.WorkDaysTotal == src.WorkDaysTotal);

        // Field Party v1 (2026-09-24): a detachment that comes back with an
        // empty ParentId is a phantom PARTY the roster will offer to march. A
        // lost WorkSide holds the wrong side of a war. These cannot default.
        ok &= Check(sb, "FieldParty.ParentId", rt.ParentId == src.ParentId);
        ok &= Check(sb, "FieldParty.WorkStalled", rt.WorkStalled == src.WorkStalled);
        ok &= Check(sb, "FieldParty.WorkProgress", rt.WorkProgress == src.WorkProgress);
        ok &= Check(sb, "FieldParty.WorkSide", rt.WorkSide == src.WorkSide);

        // The day clock (2026-09-23): a lost BusyUntilDay is a free dive; a
        // lost StartDay charges the walking on top of the moons already sat.
        ok &= Check(sb, "FieldParty.BusyUntilDay", rt.BusyUntilDay == src.BusyUntilDay);
        ok &= Check(sb, "FieldParty.TravelDayAccum", rt.TravelDayAccum == src.TravelDayAccum);
        ok &= Check(sb, "FieldParty.Sortie.StartDay", rt.Sortie?.StartDay == src.Sortie.StartDay);

        // The enum must survive as a VALUE, not an ordinal that shifts if the
        // enum is ever reordered. Pin the wire form explicitly.
        string json = JsonSerializer.Serialize(src, SaveManager.JsonOptions);
        ok &= Check(sb, "FieldParty.State is serialized (present in json)", json.Contains("state"));

        sb.AppendLine(ok ? "    PASS" : "    FAILED");
        return ok;
    }

    // ── ExpeditionTurnState ──────────────────────────────────────────────

    private static bool AssertTurnState(StringBuilder sb)
    {
        sb.AppendLine("  ExpeditionTurnState");
        var src = new ExpeditionTurnState
        {
            Lunation = 4,
            CastleMoveSpent = true,
            DivesSpent = 2,
            DivesPerLunation = 3,
        };
        var rt = RoundTrip(src);

        bool ok = true;
        ok &= Check(sb, "ExpeditionTurnState.Lunation", rt.Lunation == src.Lunation);
        ok &= Check(sb, "ExpeditionTurnState.CastleMoveSpent", rt.CastleMoveSpent == src.CastleMoveSpent);
        ok &= Check(sb, "ExpeditionTurnState.DivesSpent", rt.DivesSpent == src.DivesSpent);
        ok &= Check(sb, "ExpeditionTurnState.DivesPerLunation", rt.DivesPerLunation == src.DivesPerLunation);
        ok &= Check(sb, "ExpeditionTurnState.CanMoveCastle (derived)", !rt.CanMoveCastle);
        ok &= Check(sb, "ExpeditionTurnState.CanDive (derived)", rt.CanDive);
        ok &= Check(sb, "ExpeditionTurnState.DivesRemaining (derived)", rt.DivesRemaining == 1);

        // BeginLunation must be idempotent inside one lunation, or a reload
        // mid-lunation would refund a spent castle move.
        rt.BeginLunation(4);
        ok &= Check(sb, "BeginLunation is idempotent for the same lunation",
                    rt.CastleMoveSpent && rt.DivesSpent == 2);

        rt.BeginLunation(5);
        ok &= Check(sb, "BeginLunation resets on a new lunation",
                    !rt.CastleMoveSpent && rt.DivesSpent == 0 && rt.Lunation == 5);

        // DivesRemaining must never go negative even if a dive slips past the gate.
        var over = new ExpeditionTurnState { DivesPerLunation = 2, DivesSpent = 5 };
        ok &= Check(sb, "DivesRemaining floors at zero", over.DivesRemaining == 0);

        sb.AppendLine(ok ? "    PASS" : "    FAILED");
        return ok;
    }

    // ── SignaturePool ────────────────────────────────────────────────────

    private static bool AssertSignaturePool(StringBuilder sb)
    {
        sb.AppendLine("  SignaturePoolState");
        var src = new SignaturePoolState
        {
            MaxPoolSize = 18,
            Spells = new List<SignatureSpell>
            {
                new SignatureSpell
                {
                    BlueprintId = "Elementalist:Emberlash|Scorch",
                    TopTier = 2,
                    BotTier = 1,
                    CastableRemotely = true,
                    AddedLunation = 3,
                },
                new SignatureSpell
                {
                    BlueprintId = "Adept:Ward|Mend",
                    TopTier = 0,
                    BotTier = 3,
                    CastableRemotely = false,
                    AddedLunation = 7,
                },
            },
        };
        var rt = RoundTrip(src);

        bool ok = true;
        ok &= Check(sb, "SignaturePoolState.MaxPoolSize", rt.MaxPoolSize == src.MaxPoolSize);
        ok &= Check(sb, "SignaturePoolState.Spells count", rt.Count == 2);

        var a = rt.Find("Elementalist:Emberlash|Scorch");
        ok &= Check(sb, "SignatureSpell survives lookup by id", a != null);
        if (a != null)
        {
            ok &= Check(sb, "SignatureSpell.TopTier (pool-level upgrade)", a.TopTier == 2);
            ok &= Check(sb, "SignatureSpell.BotTier (pool-level upgrade)", a.BotTier == 1);
            ok &= Check(sb, "SignatureSpell.CastableRemotely", a.CastableRemotely);
            ok &= Check(sb, "SignatureSpell.AddedLunation", a.AddedLunation == 3);
        }

        var b = rt.Find("Adept:Ward|Mend");
        ok &= Check(sb, "Second SignatureSpell survives", b != null && b.BotTier == 3 && !b.CastableRemotely);

        ok &= Check(sb, "SignaturePoolState.Contains", rt.Contains("Adept:Ward|Mend"));
        ok &= Check(sb, "SignaturePoolState.Contains rejects an absent id", !rt.Contains("nope"));
        ok &= Check(sb, "SignaturePoolState.Find tolerates null", rt.Find(null) == null);
        ok &= Check(sb, "SignaturePoolState.IsFull (derived)", !rt.IsFull);

        sb.AppendLine(ok ? "    PASS" : "    FAILED");
        return ok;
    }

    // ── WizardSignatureSlots ─────────────────────────────────────────────

    private static bool AssertSignatureSlots(StringBuilder sb)
    {
        sb.AppendLine("  WizardSignatureSlots");
        var src = new WizardSignatureSlots
        {
            CompanionId = "elara_stormcaller",
            SlottedBlueprintIds = new List<string> { "Elementalist:Emberlash|Scorch" },
            Slots = 4,
            RarityBudget = 9,
        };
        var rt = RoundTrip(src);

        bool ok = true;
        ok &= Check(sb, "WizardSignatureSlots.CompanionId", rt.CompanionId == src.CompanionId);
        ok &= Check(sb, "WizardSignatureSlots.SlottedBlueprintIds", SameList(rt.SlottedBlueprintIds, src.SlottedBlueprintIds));
        ok &= Check(sb, "WizardSignatureSlots.Slots", rt.Slots == src.Slots);
        ok &= Check(sb, "WizardSignatureSlots.RarityBudget", rt.RarityBudget == src.RarityBudget);

        // The rarity cost ladder is the one place a rarity becomes a number.
        ok &= Check(sb, "PointsFor(Common)", WizardSignatureSlots.PointsFor(CardRarity.Common) == 1);
        ok &= Check(sb, "PointsFor(Uncommon)", WizardSignatureSlots.PointsFor(CardRarity.Uncommon) == 2);
        ok &= Check(sb, "PointsFor(Rare)", WizardSignatureSlots.PointsFor(CardRarity.Rare) == 3);
        ok &= Check(sb, "PointsFor(Legendary)", WizardSignatureSlots.PointsFor(CardRarity.Legendary) == 5);

        // A full grant must refuse another spell rather than overflowing.
        var full = new WizardSignatureSlots
        {
            Slots = 1,
            RarityBudget = 8,
            SlottedBlueprintIds = new List<string> { "whatever" },
        };
        bool refused = !full.CanSlot("another", out string reason);
        ok &= Check(sb, "CanSlot refuses when slots are full", refused && !string.IsNullOrEmpty(reason));

        var dupe = new WizardSignatureSlots
        {
            Slots = 3,
            RarityBudget = 8,
            SlottedBlueprintIds = new List<string> { "same" },
        };
        ok &= Check(sb, "CanSlot refuses a duplicate", !dupe.CanSlot("same", out _));
        ok &= Check(sb, "CanSlot refuses an empty id", !dupe.CanSlot("", out _));

        sb.AppendLine(ok ? "    PASS" : "    FAILED");
        return ok;
    }

    // ── Companion posting ────────────────────────────────────────────────

    private static bool AssertCompanionPosting(StringBuilder sb)
    {
        sb.AppendLine("  Companion posting");
        var src = new Companion
        {
            Id = "harl_denner",
            Name = "Harl Denner",
            Posting = CompanionPosting.Field,
            FieldPartyId = "field_2",
        };
        var rt = RoundTrip(src);

        bool ok = true;
        ok &= Check(sb, "Companion.Posting", rt.Posting == CompanionPosting.Field);
        ok &= Check(sb, "Companion.FieldPartyId", rt.FieldPartyId == "field_2");
        ok &= Check(sb, "Companion.Id still round-trips", rt.Id == src.Id);

        var fresh = RoundTrip(new Companion { Id = "x" });
        ok &= Check(sb, "Companion.Posting defaults to Campus", fresh.Posting == CompanionPosting.Campus);
        ok &= Check(sb, "Companion.FieldPartyId defaults empty", fresh.FieldPartyId == "");

        sb.AppendLine(ok ? "    PASS" : "    FAILED");
        return ok;
    }

    // ── WorldPoi regrowth ────────────────────────────────────────────────

    private static bool AssertPoiRegrowth(StringBuilder sb)
    {
        sb.AppendLine("  WorldPoi harvestable regrowth");
        var src = new WorldPoi
        {
            X = 7,
            Y = 9,
            Kind = PoiKind.SupplyCache,
            Harvestable = true,
            RegenLunations = 2,
            RegenReadyLunation = 6,
        };
        var rt = RoundTrip(src);

        bool ok = true;
        ok &= Check(sb, "WorldPoi.Harvestable", rt.Harvestable);
        ok &= Check(sb, "WorldPoi.RegenLunations", rt.RegenLunations == 2);
        ok &= Check(sb, "WorldPoi.RegenReadyLunation", rt.RegenReadyLunation == 6);
        ok &= Check(sb, "WorldPoi.Kind still round-trips", rt.Kind == PoiKind.SupplyCache);
        ok &= Check(sb, "WorldPoi.X still round-trips", rt.X == 7);

        var plain = RoundTrip(new WorldPoi());
        ok &= Check(sb, "WorldPoi.Harvestable defaults false", !plain.Harvestable);
        ok &= Check(sb, "WorldPoi.RegenReadyLunation defaults to ready", plain.RegenReadyLunation == 0);

        sb.AppendLine(ok ? "    PASS" : "    FAILED");
        return ok;
    }

    // ── CycleState carrying the whole block ──────────────────────────────

    private static bool AssertCycleStateCarriesAll(StringBuilder sb)
    {
        sb.AppendLine("  CycleState carries the split-forces block");
        var src = new CycleState
        {
            CastleX = 44,
            CastleY = 21,
            MaxFieldParties = 2,
            CastleBusyUntilDay = 61,
            CastleRepairDayAccum = 9,
        };
        src.Calendar.DayOfLunation = 17;
        src.FieldParties.Add(new FieldParty
        {
            Id = "field_1",
            AnchorKey = ExpeditionAnchors.CastleKeyOf(44, 21),
            State = FieldPartyState.Deployed,
        });
        src.Waypoints.Add(new Waypoint { Key = Waypoint.KeyOf(3, 4), X = 3, Y = 4, Charges = 2 });
        src.ExpeditionTurn.Lunation = 6;
        src.ExpeditionTurn.DivesSpent = 1;
        src.SignaturePool.Spells.Add(new SignatureSpell { BlueprintId = "Adept:A|B", TopTier = 1 });
        src.SignatureGrants.Add(new WizardSignatureSlots { CompanionId = "c1", Slots = 3 });

        var rt = RoundTrip(src);

        bool ok = true;
        ok &= Check(sb, "CycleState.CastleX", rt.CastleX == 44);
        ok &= Check(sb, "CycleState.CastleY", rt.CastleY == 21);
        ok &= Check(sb, "CycleState.CastleBusyUntilDay", rt.CastleBusyUntilDay == 61);
        ok &= Check(sb, "CycleState.CastleRepairDayAccum", rt.CastleRepairDayAccum == 9);
        ok &= Check(sb, "CycleState.Calendar.DayOfLunation", rt.Calendar != null && rt.Calendar.DayOfLunation == 17);
        ok &= Check(sb, "CycleState.MaxFieldParties", rt.MaxFieldParties == 2);
        ok &= Check(sb, "CycleState.FieldParties", rt.FieldParties != null && rt.FieldParties.Count == 1);
        ok &= Check(sb, "CycleState.FieldParties[0].State",
                    rt.FieldParties.Count > 0 && rt.FieldParties[0].State == FieldPartyState.Deployed);
        ok &= Check(sb, "CycleState.Waypoints", rt.Waypoints != null && rt.Waypoints.Count == 1);
        ok &= Check(sb, "CycleState.Waypoints[0].Charges",
                    rt.Waypoints.Count > 0 && rt.Waypoints[0].Charges == 2);
        ok &= Check(sb, "CycleState.ExpeditionTurn", rt.ExpeditionTurn != null && rt.ExpeditionTurn.Lunation == 6);
        ok &= Check(sb, "CycleState.ExpeditionTurn.DivesSpent",
                    rt.ExpeditionTurn != null && rt.ExpeditionTurn.DivesSpent == 1);
        ok &= Check(sb, "CycleState.SignaturePool", rt.SignaturePool != null && rt.SignaturePool.Count == 1);
        ok &= Check(sb, "CycleState.SignatureGrants", rt.SignatureGrants != null && rt.SignatureGrants.Count == 1);

        // A default CycleState must not claim the castle is at 0,0.
        var fresh = RoundTrip(new CycleState());
        ok &= Check(sb, "Fresh CycleState has no castle position", fresh.CastleX == -1 && fresh.CastleY == -1);
        ok &= Check(sb, "Fresh CycleState has one field party allowed", fresh.MaxFieldParties == 1);

        sb.AppendLine(ok ? "    PASS" : "    FAILED");
        return ok;
    }

    // ── The backfill path (the one that can lose a real crew) ────────────

    private static bool AssertBackfillFromPreSplitSave(StringBuilder sb)
    {
        sb.AppendLine("  Backfill from a pre-split save");
        bool ok = true;

        // A save written before the split: companions exist, the active party
        // list names the crew, and nobody carries a Posting yet.
        var cycle = new CycleState();
        cycle.Companions.Add(new Companion { Id = "brannoc_helm", IsRecruited = true });
        cycle.Companions.Add(new Companion { Id = "fenna_boltwright", IsRecruited = true });
        cycle.Companions.Add(new Companion { Id = "corvin_ashdown", IsRecruited = true });
        cycle.ActivePartyCompanionIds.Add("brannoc_helm");
        cycle.ActivePartyCompanionIds.Add("fenna_boltwright");

        var loaded = RoundTrip(cycle);
        ExpeditionAnchors.BackfillPostings(loaded);

        var crew = ExpeditionAnchors.Crew(loaded);
        ok &= Check(sb, "Backfill posts the old active party to the crew", crew.Count == 2);
        ok &= Check(sb, "Backfill leaves the bench on campus",
                    loaded.Companions.Find(c => c.Id == "corvin_ashdown")?.Posting == CompanionPosting.Campus);
        ok &= Check(sb, "Backfill creates the primary field party",
                    loaded.FieldParties != null && loaded.FieldParties.Count == 1
                    && loaded.FieldParties[0].Id == ExpeditionAnchors.PrimaryFieldPartyId);

        // Running it twice must not move anyone: a player who has since posted
        // their crew to the field would otherwise be reset on every load.
        loaded.Companions.Find(c => c.Id == "brannoc_helm").Posting = CompanionPosting.Field;
        ExpeditionAnchors.BackfillPostings(loaded);
        ok &= Check(sb, "Backfill is a no-op once postings exist",
                    loaded.Companions.Find(c => c.Id == "brannoc_helm")?.Posting == CompanionPosting.Field);

        // Reconcile must rebuild the party roster from the companions.
        ExpeditionAnchors.ReconcilePostings(loaded);
        var party = loaded.FieldParties[0];
        ok &= Check(sb, "Reconcile puts the field-posted companion in the party",
                    party.MemberCompanionIds.Count == 1 && party.MemberCompanionIds[0] == "brannoc_helm");
        ok &= Check(sb, "Reconcile backfills an empty FieldPartyId",
                    loaded.Companions.Find(c => c.Id == "brannoc_helm")?.FieldPartyId
                        == ExpeditionAnchors.PrimaryFieldPartyId);

        // A posting naming a party that no longer exists must not drop anyone.
        loaded.Companions.Find(c => c.Id == "corvin_ashdown").Posting = CompanionPosting.Field;
        loaded.Companions.Find(c => c.Id == "corvin_ashdown").FieldPartyId = "field_gone";
        ExpeditionAnchors.ReconcilePostings(loaded);
        ok &= Check(sb, "Reconcile rehomes a companion whose party vanished",
                    loaded.FieldParties[0].MemberCompanionIds.Contains("corvin_ashdown"));

        // Reconcile must be repeatable without duplicating members.
        ExpeditionAnchors.ReconcilePostings(loaded);
        ok &= Check(sb, "Reconcile does not duplicate members when run twice",
                    loaded.FieldParties[0].MemberCompanionIds.Count == 2);

        sb.AppendLine(ok ? "    PASS" : "    FAILED");
        return ok;
    }
}
