using Godot;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

// ============================================================
// CharterSaveAssert.cs
//
// Purpose:        Save round-trip assertions for the charter structs
//                 (campus_building_upgrades_design_v1 §2b): CharterEntry,
//                 and CycleState.Charters / CharterFillsUsed. Also the later
//                 campus slices' fields: school seats, the portal strike,
//                 and campus corruption (§11). Same
//                 serializer, same contract as ArmorySaveAssert: build
//                 with distinctive non-default values, serialize with
//                 SaveManager's options, deserialize, compare field by
//                 field, and check the additive defaults on legacy JSON.
// Layer:          Verification
// Collaborators:  Charters.cs (CharterEntry), CycleState.cs,
//                 SaveManager.cs (JsonOptions), CampusGuildPanel.cs
//                 (the "Assert Round-Trips" debug button)
// ============================================================

public static class CharterSaveAssert
{
    /// <summary>Run every charter round-trip assertion. Prints a PASS/FAIL report
    /// and PushErrors on any failure. Returns true only if all passed.</summary>
    public static bool AssertAll()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== CHARTER SAVE ROUND-TRIP ASSERTIONS ===");

        bool ok = AssertCharterEntry(sb);
        ok &= AssertCycleCharters(sb);
        ok &= AssertSeatFields(sb);
        ok &= AssertPortalFields(sb);
        ok &= AssertBlightFields(sb);
        ok &= AssertCarriedWeapon(sb);
        ok &= AssertRefinement(sb);

        sb.AppendLine(ok
            ? "RESULT: ALL PASSED. Charter save-adjacent structs round-trip clean."
            : "RESULT: FAILURES ABOVE. A field is being dropped or renamed.");
        GD.Print(sb.ToString());

        if (!ok)
        {
            GD.PushError("[CharterSaveAssert] Round-trip assertion FAILED. See Output panel.");
        }
        return ok;
    }

    private static bool AssertCharterEntry(StringBuilder sb)
    {
        var src = new CharterEntry
        {
            BuildingId = "scribes_tower",
            DoctrineId = "glasswright",
            ActiveFromLunation = 7,
        };
        var rt = RoundTrip(src);

        bool ok = rt != null;
        if (rt != null)
        {
            ok &= Check(sb, "CharterEntry.BuildingId", rt.BuildingId == src.BuildingId);
            ok &= Check(sb, "CharterEntry.DoctrineId", rt.DoctrineId == src.DoctrineId);
            ok &= Check(sb, "CharterEntry.ActiveFromLunation", rt.ActiveFromLunation == src.ActiveFromLunation);
        }
        else
        {
            sb.AppendLine("    FAIL: CharterEntry deserialized to null.");
        }

        sb.AppendLine(ok ? "  CharterEntry: PASS" : "  CharterEntry: FAIL");
        return ok;
    }

    private static bool AssertCycleCharters(StringBuilder sb)
    {
        var src = new CycleState
        {
            CharterFillsUsed = 3,
            Charters = new List<CharterEntry>
            {
                new CharterEntry { BuildingId = "scribes_tower", DoctrineId = "wandwright", ActiveFromLunation = 2 },
                new CharterEntry { BuildingId = "enchanters_workshop", DoctrineId = "unbinding_floor", ActiveFromLunation = 5 },
            },
        };
        var rt = RoundTrip(src);

        bool ok = rt != null;
        if (rt != null)
        {
            ok &= Check(sb, "CycleState.CharterFillsUsed", rt.CharterFillsUsed == src.CharterFillsUsed);
            ok &= Check(sb, "CycleState.Charters count", rt.Charters != null && rt.Charters.Count == 2);
            if (rt.Charters != null && rt.Charters.Count == 2)
            {
                ok &= Check(sb, "CycleState.Charters[1].DoctrineId",
                    rt.Charters[1].DoctrineId == "unbinding_floor" && rt.Charters[1].ActiveFromLunation == 5);
            }
        }
        else
        {
            sb.AppendLine("    FAIL: CycleState deserialized to null.");
        }

        // The additive default: a cycle saved before charters existed reads as empty.
        var legacy = JsonSerializer.Deserialize<CycleState>("{\"cycleNumber\":2}", SaveManager.JsonOptions);
        ok &= Check(sb, "CycleState.Charters empty (not null) on legacy JSON",
            legacy != null && legacy.Charters != null && legacy.Charters.Count == 0);
        ok &= Check(sb, "CycleState.CharterFillsUsed default 0 on legacy JSON",
            legacy != null && legacy.CharterFillsUsed == 0);

        sb.AppendLine(ok ? "  CycleState charters: PASS" : "  CycleState charters: FAIL");
        return ok;
    }

    /// <summary>School seats (slice 2): the Crucible's front on CycleState and the
    /// blight facet site index on ShardZone, plus their legacy defaults.</summary>
    private static bool AssertSeatFields(StringBuilder sb)
    {
        var cycle = RoundTrip(new CycleState { CrucibleFront = "Storm" });
        bool ok = Check(sb, "CycleState.CrucibleFront", cycle != null && cycle.CrucibleFront == "Storm");

        var zone = RoundTrip(new ShardZone { FragmentKey = "primal", FacetSitePoiIndex = 41 });
        ok &= Check(sb, "ShardZone.FacetSitePoiIndex", zone != null && zone.FacetSitePoiIndex == 41);

        var legacyCycle = JsonSerializer.Deserialize<CycleState>("{\"cycleNumber\":3}", SaveManager.JsonOptions);
        ok &= Check(sb, "CycleState.CrucibleFront default empty on legacy JSON",
            legacyCycle != null && legacyCycle.CrucibleFront == "");
        var legacyZone = JsonSerializer.Deserialize<ShardZone>("{\"fragmentKey\":\"axiom\"}", SaveManager.JsonOptions);
        ok &= Check(sb, "ShardZone.FacetSitePoiIndex default -1 on legacy JSON",
            legacyZone != null && legacyZone.FacetSitePoiIndex == -1);

        sb.AppendLine(ok ? "  School seat fields: PASS" : "  School seat fields: FAIL");
        return ok;
    }

    /// <summary>Teleport Sigil (slice 3): the portal-strike fields on CycleState.</summary>
    private static bool AssertPortalFields(StringBuilder sb)
    {
        var src = new CycleState
        {
            PortalKnowledge = new Dictionary<string, int> { { "kingdom_a", 4 }, { "kingdom_b", 1 } },
            PendingPortalStrikeKingdomId = "kingdom_a",
            PortalStrikeLaunched = true,
        };
        var rt = RoundTrip(src);
        bool ok = rt != null;
        if (rt != null)
        {
            ok &= Check(sb, "CycleState.PortalKnowledge", rt.PortalKnowledge != null
                && rt.PortalKnowledge.TryGetValue("kingdom_a", out int a) && a == 4
                && rt.PortalKnowledge.TryGetValue("kingdom_b", out int b) && b == 1);
            ok &= Check(sb, "CycleState.PendingPortalStrikeKingdomId", rt.PendingPortalStrikeKingdomId == "kingdom_a");
            ok &= Check(sb, "CycleState.PortalStrikeLaunched", rt.PortalStrikeLaunched);
        }
        var legacy = JsonSerializer.Deserialize<CycleState>("{\"cycleNumber\":4}", SaveManager.JsonOptions);
        ok &= Check(sb, "CycleState portal fields default on legacy JSON",
            legacy != null && legacy.PortalKnowledge != null && legacy.PortalKnowledge.Count == 0
            && legacy.PendingPortalStrikeKingdomId == "" && !legacy.PortalStrikeLaunched);

        sb.AppendLine(ok ? "  Portal strike fields: PASS" : "  Portal strike fields: FAIL");
        return ok;
    }

    /// <summary>Campus corruption (design §11): the tile, building and item fields,
    /// their legacy defaults, and that a district relayout keeps scarred ground.</summary>
    private static bool AssertBlightFields(StringBuilder sb)
    {
        var tile = RoundTrip(new CampusTileSaveData
        {
            Q = 2, R = -1, Ground = "Rubble", Corruption = 2, RubbleOf = "enchanters_workshop", PriorGround = "Plaza",
        });
        bool ok = Check(sb, "CampusTileSaveData.Corruption/RubbleOf/PriorGround", tile != null
            && tile.Corruption == 2 && tile.RubbleOf == "enchanters_workshop"
            && tile.PriorGround == "Plaza" && tile.Ground == "Rubble");

        var building = RoundTrip(new BuildingSaveData { Id = "scribes_tower", Tier = 2, BlightLevel = 3 });
        ok &= Check(sb, "BuildingSaveData.BlightLevel", building != null && building.BlightLevel == 3);
        ok &= Check(sb, "BuildingSaveData overrun is not functional",
            building != null && !new BuildingSaveData { Tier = 2, IsPlaced = true, BlightLevel = 3 }.IsFunctional
            && new BuildingSaveData { Tier = 2, IsPlaced = true, BlightLevel = 2 }.IsFunctional);

        var item = RoundTrip(new ItemInstance { DefinitionId = "spellglass", BoundCardId = "fireball", Cracked = true });
        ok &= Check(sb, "ItemInstance.Cracked", item != null && item.Cracked);

        var legacyTile = JsonSerializer.Deserialize<CampusTileSaveData>("{\"q\":1,\"r\":1}", SaveManager.JsonOptions);
        ok &= Check(sb, "CampusTileSaveData blight defaults on legacy JSON", legacyTile != null
            && legacyTile.Corruption == 0 && legacyTile.RubbleOf == "" && legacyTile.PriorGround == "");
        var legacyBuilding = JsonSerializer.Deserialize<BuildingSaveData>("{\"id\":\"armory\",\"tier\":1}", SaveManager.JsonOptions);
        ok &= Check(sb, "BuildingSaveData.BlightLevel default 0 on legacy JSON",
            legacyBuilding != null && legacyBuilding.BlightLevel == 0);
        var legacyItem = JsonSerializer.Deserialize<ItemInstance>("{\"definitionId\":\"spellglass\"}", SaveManager.JsonOptions);
        ok &= Check(sb, "ItemInstance.Cracked default false on legacy JSON", legacyItem != null && !legacyItem.Cracked);

        // A district unlock rebuilds every tile; the scars must survive it.
        var map = CampusMapSaveData.GenerateDefault();
        var scar = map.Tiles[0];
        scar.Corruption = 3;
        scar.PriorGround = scar.Ground;
        scar.Ground = "Rubble";
        scar.RubbleOf = "armory";
        int q = scar.Q, r = scar.R;
        bool unlocked = false;
        foreach (var d in map.Districts)
        {
            if (!d.Unlocked)
            {
                unlocked = map.UnlockDistrict(d.Q, d.R);
                break;
            }
        }
        var after = map.Tiles.Find(t => t.Q == q && t.R == r);
        ok &= Check(sb, "CampusMapSaveData relayout keeps corruption and rubble", unlocked && after != null
            && after.Corruption == 3 && after.Ground == "Rubble" && after.RubbleOf == "armory");

        sb.AppendLine(ok ? "  Campus blight fields: PASS" : "  Campus blight fields: FAIL");
        return ok;
    }

    /// <summary>Proving Grounds (design §12): the carried second weapon on the
    /// loadout, its legacy default, and the armory rules around it.</summary>
    private static bool AssertCarriedWeapon(StringBuilder sb)
    {
        var armory = new ArmoryData();
        armory.OwnedItems.Add(new ItemInstance { InstanceId = "w-1", DefinitionId = "iron_sword", Slot = "Weapon" });
        armory.OwnedItems.Add(new ItemInstance { InstanceId = "w-2", DefinitionId = "hunters_bow", Slot = "Weapon" });
        armory.OwnedItems.Add(new ItemInstance { InstanceId = "t-1", DefinitionId = "warriors_sigil", Slot = "Trinket" });
        armory.Equip("torrin", "w-1");

        bool carried = armory.EquipSecondWeapon("torrin", "w-2");
        bool refusedHeld = !armory.EquipSecondWeapon("brannoc", "w-1");      // in torrin's hand
        bool refusedTrinket = !armory.EquipSecondWeapon("brannoc", "t-1");   // not a weapon
        bool ok = Check(sb, "ArmoryData.EquipSecondWeapon accepts a free weapon", carried);
        ok &= Check(sb, "ArmoryData.EquipSecondWeapon refuses a held weapon and a non-weapon", refusedHeld && refusedTrinket);
        ok &= Check(sb, "ArmoryData.GetUnequipped excludes a carried weapon",
            !armory.GetUnequipped().Exists(i => i.InstanceId == "w-2"));

        var rt = RoundTrip(armory);
        ok &= Check(sb, "UnitLoadout.SecondWeaponInstanceId", rt != null
            && rt.GetLoadout("torrin").SecondWeaponInstanceId == "w-2"
            && rt.GetLoadout("torrin").WeaponInstanceId == "w-1");

        armory.RemoveItem("w-2");
        ok &= Check(sb, "ArmoryData.RemoveItem clears the carried weapon",
            armory.GetLoadout("torrin").SecondWeaponInstanceId == null);

        var legacy = JsonSerializer.Deserialize<UnitLoadout>("{\"weaponInstanceId\":\"w-9\"}", SaveManager.JsonOptions);
        ok &= Check(sb, "UnitLoadout.SecondWeaponInstanceId null on legacy JSON",
            legacy != null && legacy.SecondWeaponInstanceId == null && legacy.WeaponInstanceId == "w-9");

        sb.AppendLine(ok ? "  Carried weapon (Proving Grounds): PASS" : "  Carried weapon (Proving Grounds): FAIL");
        return ok;
    }

    /// <summary>Refinement (Scriptorum doctrine, design §15): the card flag, the
    /// cycle counter, the seventh point, and their legacy defaults.</summary>
    private static bool AssertRefinement(StringBuilder sb)
    {
        var card = RoundTrip(new OwnedCard { BlueprintId = "fireball", InstanceId = "c-1", TopTier = 4, BotTier = 3, PointsSpent = 6, Refined = true });
        bool ok = Check(sb, "OwnedCard.Refined", card != null && card.Refined);
        ok &= Check(sb, "OwnedCard refined budget is 7 points", card != null && card.PointsRemaining == 1 && card.CanUpgradeBot);
        ok &= Check(sb, "OwnedCard unrefined budget is 6 points",
            new OwnedCard { TopTier = 4, BotTier = 3, PointsSpent = 6 }.PointsRemaining == 0);
        var cycle = RoundTrip(new CycleState { RefinementsUsed = 1 });
        ok &= Check(sb, "CycleState.RefinementsUsed", cycle != null && cycle.RefinementsUsed == 1);

        var legacyCard = JsonSerializer.Deserialize<OwnedCard>("{\"blueprintId\":\"fireball\"}", SaveManager.JsonOptions);
        ok &= Check(sb, "OwnedCard.Refined false on legacy JSON", legacyCard != null && !legacyCard.Refined);
        var legacyCycle = JsonSerializer.Deserialize<CycleState>("{\"cycleNumber\":5}", SaveManager.JsonOptions);
        ok &= Check(sb, "CycleState.RefinementsUsed 0 on legacy JSON", legacyCycle != null && legacyCycle.RefinementsUsed == 0);

        sb.AppendLine(ok ? "  Refinement fields: PASS" : "  Refinement fields: FAIL");
        return ok;
    }

    // ── Helpers (mirror ArmorySaveAssert) ─────────────────────────────────

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
}
