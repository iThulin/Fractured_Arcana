using Godot;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

// ============================================================
// ArmorySaveAssert.cs
//
// Purpose:        Save round-trip assertions for the armory structs
//                 that Edge M6 extended: ItemInstance (Charges,
//                 BoundCardId), UnitLoadout (BeltInstanceIds) and
//                 ArmoryData carrying both. Same serializer, same
//                 contract as ProgressionSaveAssert and
//                 CouncilSaveAssert: build a struct with distinctive
//                 non-default values, serialize with SaveManager's
//                 options, deserialize, compare field by field.
// Layer:          Verification
// Collaborators:  ItemDefinition.cs (ItemInstance, UnitLoadout),
//                 ItemDatabase.cs (ArmoryData), SaveManager.cs
//                 (JsonOptions), CampusGuildPanel.cs (the debug button)
// Usage:          CampusGuildPanel "Assert Round-Trips".
// ============================================================

public static class ArmorySaveAssert
{
    /// <summary>Run every armory round-trip assertion. Prints a PASS/FAIL report
    /// and PushErrors on any failure. Returns true only if all passed.</summary>
    public static bool AssertAll()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== ARMORY SAVE ROUND-TRIP ASSERTIONS ===");

        bool ok = AssertItemInstance(sb);
        ok &= AssertUnitLoadout(sb);
        ok &= AssertArmoryData(sb);
        ok &= AssertCompanionWeapon(sb);

        sb.AppendLine(ok
            ? "RESULT: ALL PASSED. Armory save-adjacent structs round-trip clean."
            : "RESULT: FAILURES ABOVE. A field is being dropped or renamed.");
        GD.Print(sb.ToString());

        if (!ok)
            GD.PushError("[ArmorySaveAssert] Round-trip assertion FAILED. See Output panel.");

        return ok;
    }

    private static bool AssertItemInstance(StringBuilder sb)
    {
        var src = new ItemInstance
        {
            DefinitionId = "wand_of_missiles",
            InstanceId = "assert-instance-1",
            Name = "Wand of Missiles",
            Slot = "Trinket",
            UnitClass = "Any",
            Rarity = "Uncommon",
            GoldValue = 140,
            EnchantKey = "keen",
            EnchantValue = 2,
            EnchantParam = "fire",
            EnchantTrigger = "onAttack",
            EnchantSealed = true,
            DrawbackKey = "brittle",
            DrawbackValue = 1,
            BlightBonus = 1,
            Charges = 2,
            BoundCardId = "adept_magic_missile",
        };
        var rt = RoundTrip(src);

        bool ok = rt != null;
        if (rt != null)
        {
            ok &= Check(sb, "ItemInstance.DefinitionId", rt.DefinitionId == src.DefinitionId);
            ok &= Check(sb, "ItemInstance.InstanceId", rt.InstanceId == src.InstanceId);
            ok &= Check(sb, "ItemInstance.Slot", rt.Slot == src.Slot);
            ok &= Check(sb, "ItemInstance.EnchantKey", rt.EnchantKey == src.EnchantKey);
            ok &= Check(sb, "ItemInstance.EnchantSealed", rt.EnchantSealed == src.EnchantSealed);
            ok &= Check(sb, "ItemInstance.DrawbackKey", rt.DrawbackKey == src.DrawbackKey);
            ok &= Check(sb, "ItemInstance.BlightBonus", rt.BlightBonus == src.BlightBonus);
            ok &= Check(sb, "ItemInstance.Charges", rt.Charges == src.Charges);
            ok &= Check(sb, "ItemInstance.BoundCardId", rt.BoundCardId == src.BoundCardId);
        }
        else
        {
            sb.AppendLine("    FAIL: ItemInstance deserialized to null.");
        }

        // The additive default: an instance written before M6 must read as uncharged.
        var legacy = JsonSerializer.Deserialize<ItemInstance>(
            "{\"definitionId\":\"iron_sword\",\"instanceId\":\"legacy-1\",\"slot\":\"Weapon\"}",
            SaveManager.JsonOptions);
        ok &= Check(sb, "ItemInstance.Charges default -1 on legacy JSON", legacy != null && legacy.Charges == -1);
        ok &= Check(sb, "ItemInstance.BoundCardId default empty on legacy JSON",
            legacy != null && string.IsNullOrEmpty(legacy.BoundCardId));

        sb.AppendLine(ok ? "  ItemInstance: PASS" : "  ItemInstance: FAIL");
        return ok;
    }

    private static bool AssertUnitLoadout(StringBuilder sb)
    {
        var src = new UnitLoadout
        {
            WeaponInstanceId = "w-1",
            ArmorInstanceId = null,
            TrinketInstanceId = "t-1",
            BeltInstanceIds = new List<string> { "b-1", "b-2" },
        };
        var rt = RoundTrip(src);

        bool ok = rt != null;
        if (rt != null)
        {
            ok &= Check(sb, "UnitLoadout.WeaponInstanceId", rt.WeaponInstanceId == src.WeaponInstanceId);
            ok &= Check(sb, "UnitLoadout.ArmorInstanceId null", rt.ArmorInstanceId == null);
            ok &= Check(sb, "UnitLoadout.TrinketInstanceId", rt.TrinketInstanceId == src.TrinketInstanceId);
            ok &= Check(sb, "UnitLoadout.BeltInstanceIds", SameList(rt.BeltInstanceIds, src.BeltInstanceIds));
        }
        else
        {
            sb.AppendLine("    FAIL: UnitLoadout deserialized to null.");
        }

        var legacy = JsonSerializer.Deserialize<UnitLoadout>(
            "{\"weaponInstanceId\":\"w-9\"}", SaveManager.JsonOptions);
        ok &= Check(sb, "UnitLoadout.BeltInstanceIds empty (not null) on legacy JSON",
            legacy != null && legacy.BeltInstanceIds != null && legacy.BeltInstanceIds.Count == 0);

        sb.AppendLine(ok ? "  UnitLoadout: PASS" : "  UnitLoadout: FAIL");
        return ok;
    }

    private static bool AssertArmoryData(StringBuilder sb)
    {
        var src = new ArmoryData();
        src.OwnedItems.Add(new ItemInstance { DefinitionId = "healing_draught", InstanceId = "p-1", Slot = "Consumable" });
        src.OwnedItems.Add(new ItemInstance { DefinitionId = "whetstone", InstanceId = "p-2", Slot = "Consumable" });
        src.OwnedItems.Add(new ItemInstance { DefinitionId = "wand_of_missiles", InstanceId = "t-1", Slot = "Trinket", Charges = 1 });
        var lo = src.GetLoadout("torrin");
        lo.TrinketInstanceId = "t-1";
        bool belt1 = src.EquipBelt("torrin", "p-1");
        bool belt2 = src.EquipBelt("torrin", "p-2");
        bool belt3 = src.EquipBelt("torrin", "p-1");   // already carried: refused

        bool ok = Check(sb, "ArmoryData.EquipBelt first", belt1);
        ok &= Check(sb, "ArmoryData.EquipBelt second", belt2);
        ok &= Check(sb, "ArmoryData.EquipBelt duplicate refused", !belt3);
        ok &= Check(sb, "ArmoryData.GetUnequipped excludes belted", src.GetUnequipped().Count == 0);

        var rt = RoundTrip(src);
        if (rt != null)
        {
            ok &= Check(sb, "ArmoryData.OwnedItems count", rt.OwnedItems.Count == 3);
            ok &= Check(sb, "ArmoryData belt survives", SameList(rt.GetLoadout("torrin").BeltInstanceIds, lo.BeltInstanceIds));
            ok &= Check(sb, "ArmoryData wand charges survive", rt.GetInstance("t-1")?.Charges == 1);
            ok &= Check(sb, "ArmoryData.BeltCarrier", rt.BeltCarrier("p-2") == "torrin");
            rt.RemoveItem("p-1");
            ok &= Check(sb, "ArmoryData.RemoveItem clears belt", !rt.GetLoadout("torrin").BeltInstanceIds.Contains("p-1"));
        }
        else
        {
            ok = false;
            sb.AppendLine("    FAIL: ArmoryData deserialized to null.");
        }

        sb.AppendLine(ok ? "  ArmoryData: PASS" : "  ArmoryData: FAIL");
        return ok;
    }

    /// <summary>D5 (2026-09-28): the starting-weapon fields on Companion.</summary>
    private static bool AssertCompanionWeapon(StringBuilder sb)
    {
        var src = new Companion
        {
            Id = "assert_fighter",
            Name = "Assert Fighter",
            UnitClass = "Fighter",
            IsRecruited = true,
            StartingWeaponId = "barbed_spear",
            StartingWeaponGranted = true,
        };
        var rt = RoundTrip(src);
        bool ok = rt != null;
        if (rt != null)
        {
            ok &= Check(sb, "Companion.StartingWeaponId", rt.StartingWeaponId == src.StartingWeaponId);
            ok &= Check(sb, "Companion.StartingWeaponGranted", rt.StartingWeaponGranted == src.StartingWeaponGranted);
        }
        else
        {
            sb.AppendLine("    FAIL: Companion deserialized to null.");
        }
        var legacy = JsonSerializer.Deserialize<Companion>(
            "{\"id\":\"legacy\",\"unitClass\":\"Fighter\"}", SaveManager.JsonOptions);
        ok &= Check(sb, "Companion.StartingWeaponGranted default false on legacy JSON",
            legacy != null && !legacy.StartingWeaponGranted && string.IsNullOrEmpty(legacy.StartingWeaponId));
        sb.AppendLine(ok ? "  Companion (starting weapon): PASS" : "  Companion (starting weapon): FAIL");
        return ok;
    }

    // ── Helpers (mirror ProgressionSaveAssert: same serializer, same contract) ──

    private static T RoundTrip<T>(T obj)
    {
        string json = JsonSerializer.Serialize(obj, SaveManager.JsonOptions);
        return JsonSerializer.Deserialize<T>(json, SaveManager.JsonOptions);
    }

    private static bool Check(StringBuilder sb, string field, bool equal)
    {
        if (!equal)
            sb.AppendLine($"    FAIL: {field} did not survive the round-trip.");
        return equal;
    }

    private static bool SameList(List<string> a, List<string> b)
    {
        if (a == null || b == null || a.Count != b.Count)
            return false;
        for (int i = 0; i < a.Count; i++)
            if (a[i] != b[i])
                return false;
        return true;
    }
}
