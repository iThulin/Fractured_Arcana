using Godot;
using System.Text;

// ============================================================
// ConcordDebug.cs
//
// Purpose:        Verification tooling for the espionage E1c test
//                 session. Two read-only dumps:
//                   DumpNodes()  lists every Veiled Concord node in the
//                                  world: tile, host kingdom,
//                                  discovered flag, and the DERIVED
//                                  broker archetype. The direct
//                                  assertion surface for "the
//                                  Concord scattered coherently."
//                   DumpShadow() prints the espionage state on
//                                  CouncilState: informant roster
//                                  (role/cover/access/placement),
//                                  live contracts, Favor, Marked,
//                                  and the DERIVED Concord standing
//                                  band. The pre/post surface for
//                                  save-load round-trip checks.
//                 Both print to the Output panel. Read-only: neither
//                 mutates state nor marks the save dirty.
// Layer:          System (debug)
// Collaborators:  SaveManager.cs (ActiveSave.Cycle),
//                 WorldData.cs (Pois), CouncilState.cs (espionage
//                 fields), ShadowState.cs (derivations)
// See:            espionage_veiled_concord_spec_v1.md §10 (E1c)
//
// Usage: wire to the CampusScreen debug panel, or call directly:
//   ConcordDebug.DumpNodes();
//   ConcordDebug.DumpShadow();
// ============================================================

public static class ConcordDebug
{
    /// <summary>Print every Concord node with its derived broker archetype.</summary>
    public static void DumpNodes()
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        var world = cycle?.World;
        if (world == null)
        {
            GD.Print("[ConcordDebug] No active world.");
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("\n=== VEILED CONCORD NODES ===");

        int count = 0;
        foreach (var poi in world.Pois)
        {
            if (poi.Kind != PoiKind.Concord) { continue; }
            count++;
            string broker = ShadowVocab.BrokerArchetypeFor(cycle.WorldSeed, poi.X, poi.Y);
            string host = string.IsNullOrEmpty(poi.KingdomId) ? "(neutral)" : poi.KingdomId;
            sb.AppendLine($"  node #{count}: ({poi.X},{poi.Y})  host={host}  " +
                          $"broker={broker}  discovered={poi.Discovered}");
        }

        sb.AppendLine(count == 0
            ? "  (none placed; check ConcordGenerator ran and had candidate tiles)"
            : $"  total: {count} node(s), seed={cycle.WorldSeed}");
        GD.Print(sb.ToString());
    }

    /// <summary>Print the espionage state: network roster, contracts, currencies.</summary>
    public static void DumpShadow()
    {
        var council = SaveManager.ActiveSave?.Cycle?.Council;
        if (council == null)
        {
            GD.Print("[ConcordDebug] No active council/espionage state.");
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("\n=== SHADOW LEDGER ===");
        sb.AppendLine($"  Concord: contacted={council.ConcordContacted}  " +
                      $"favor={council.ConcordFavor}  marked={council.Marked}/{ShadowVocab.MarkedMax}  " +
                      $"dealings={council.ConcordDealings}  " +
                      $"band={ShadowVocab.StandingBand(council.ConcordDealings)}");

        sb.AppendLine($"  Informants ({council.Informants.Count}):");
        foreach (var inf in council.Informants)
        {
            string embed = !string.IsNullOrEmpty(inf.CourtierId)
                ? $"court:{inf.CourtierId}"
                : (!string.IsNullOrEmpty(inf.WarfrontId) ? $"war:{inf.WarfrontId}" : "kingdom");
            string handler = string.IsNullOrEmpty(inf.HandlerCompanionId)
                ? "unhandled" : inf.HandlerCompanionId;
            sb.AppendLine($"    - {inf.Id}  {inf.Role}  {inf.KingdomId}/{embed}  " +
                          $"cover={inf.Cover}/{ShadowVocab.CoverMax}  access={inf.Access}  " +
                          $"handler={handler}");
        }

        sb.AppendLine($"  Contracts ({council.ConcordContracts.Count}):");
        foreach (var c in council.ConcordContracts)
        {
            string dir = c.AgainstPlayer ? "AGAINST-GUILD" : "guild";
            sb.AppendLine($"    - {c.Id}  {c.ContractType}  {dir}  " +
                          $"target={c.TargetKingdomId}/{c.TargetId}  " +
                          $"lun={c.LunationsRemaining}  favor={c.FavorPaid}");
        }

        GD.Print(sb.ToString());
    }

    // ── E2 verification helpers (plant → tick → dump) ────────────────────

    /// <summary>Plant a Watcher in the first court's kingdom, so a tick or two
    /// later DumpShadow shows it charting + ripening, and (with a Spymaster
    /// present) being hunted. Prints what it planted.</summary>
    public static void DebugPlantWatcher()
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        var council = cycle?.Council;
        if (council == null || council.Courts.Count == 0)
        {
            GD.Print("[ConcordDebug] No courts to plant into.");
            return;
        }

        string kingdomId = null;
        foreach (var kid in council.Courts.Keys) { kingdomId = kid; break; }

        var inf = ShadowTick.PlantInformant(cycle, kingdomId,
            ShadowVocab.RoleWatcher, ShadowVocab.CoverStartTurned);
        GD.Print(inf != null
            ? $"[ConcordDebug] Planted Watcher {inf.Id} in {kingdomId} " +
              $"(cover {inf.Cover}, access {inf.Access})."
            : $"[ConcordDebug] Failed to plant in {kingdomId}.");
    }

    /// <summary>Make first contact with the Concord (flip the gate + discover the
    /// nearest node), so a contacted Cutout fences intel for Favor.</summary>
    public static void DebugContactConcord()
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        var council = cycle?.Council;
        var world = cycle?.World;
        if (council == null || world == null)
        {
            GD.Print("[ConcordDebug] No active cycle.");
            return;
        }

        council.ConcordContacted = true;
        int discovered = 0;
        foreach (var poi in world.Pois)
        {
            if (poi.Kind == PoiKind.Concord && !poi.Discovered)
            {
                poi.Discovered = true;
                discovered++;
                break;
            }
        }
        SaveManager.MarkDirty();
        GD.Print($"[ConcordDebug] Concord contacted; discovered {discovered} node.");
    }

    // ── E3 marketplace verification ──────────────────────────────────────

    /// <summary>Grant test Favor so buy-side contracts can be exercised without
    /// grinding Cutout yield.</summary>
    public static void DebugGrantFavor(int amount = 50)
    {
        var council = SaveManager.ActiveSave?.Cycle?.Council;
        if (council == null) { GD.Print("[ConcordDebug] No council."); return; }
        council.ConcordFavor += amount;
        SaveManager.MarkDirty();
        GD.Print($"[ConcordDebug] +{amount} favor ({council.ConcordFavor} banked).");
    }

    /// <summary>Fence the first known secret found across the courts.</summary>
    public static void DebugSellSecret()
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        var council = cycle?.Council;
        if (council == null) { GD.Print("[ConcordDebug] No council."); return; }

        foreach (var kv in council.Courts)
        {
            foreach (var c in kv.Value.Courtiers)
            {
                if (c.SecretKnown)
                {
                    var r = ShadowMarket.SellSecret(cycle, kv.Key, c.Id);
                    GD.Print($"[ConcordDebug] SellSecret: {r.Message}");
                    return;
                }
            }
        }
        GD.Print("[ConcordDebug] No known secret to sell. Dig one first (Cutout / Gather Intel).");
    }

    public static void DebugCommissionPlant()
        => CommissionInFirstCourt("plant");

    public static void DebugCommissionIntel()
        => CommissionInFirstCourt("intel");

    public static void DebugCommissionTheft()
        => CommissionInFirstCourt("theft");

    private static void CommissionInFirstCourt(string which)
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        var council = cycle?.Council;
        if (council == null || council.Courts.Count == 0)
        {
            GD.Print("[ConcordDebug] No courts.");
            return;
        }

        string kingdomId = null;
        CourtState court = null;
        foreach (var kv in council.Courts) { kingdomId = kv.Key; court = kv.Value; break; }
        string courtierId = court.Courtiers.Count > 0 ? court.Courtiers[0].Id : "";

        ShadowMarketResult r = which switch
        {
            "plant" => ShadowMarket.CommissionPlantAsset(cycle, kingdomId),
            "intel" => ShadowMarket.CommissionPurchaseIntel(cycle, kingdomId),
            "theft" => ShadowMarket.CommissionTheft(cycle, kingdomId, courtierId),
            "sabotage_siege" => ShadowMarket.CommissionSabotage(cycle, kingdomId, ShadowVocab.SabotageSiege),
            "sabotage_corruption" => ShadowMarket.CommissionSabotage(cycle, kingdomId, ShadowVocab.SabotageCorruption),
            "extraction" => ShadowMarket.CommissionExtraction(cycle, kingdomId),
            _ => ShadowMarketResult.Fail("unknown"),
        };
        GD.Print($"[ConcordDebug] Commission {which}: {r.Message}");
    }

    // ── E4 sabotage / false-echo verification ────────────────────────────

    public static void DebugBuySabotageSiege() => CommissionInFirstCourt("sabotage_siege");
    public static void DebugBuySabotageCorruption() => CommissionInFirstCourt("sabotage_corruption");

    /// <summary>Plant a Saboteur in the first court's kingdom.</summary>
    public static void DebugPlantSaboteur()
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        var council = cycle?.Council;
        if (council == null || council.Courts.Count == 0)
        {
            GD.Print("[ConcordDebug] No courts.");
            return;
        }
        string kingdomId = null;
        foreach (var kid in council.Courts.Keys) { kingdomId = kid; break; }
        var inf = ShadowTick.PlantInformant(cycle, kingdomId,
            ShadowVocab.RoleSaboteur, ShadowVocab.CoverStartTurned);
        GD.Print(inf != null
            ? $"[ConcordDebug] Planted Saboteur {inf.Id} in {kingdomId}."
            : $"[ConcordDebug] Failed to plant saboteur.");
    }

    /// <summary>First Saboteur strikes (corruption delay: always has a §4-gated
    /// effect; siege needs an open front).</summary>
    public static void DebugSaboteurStrike()
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        var council = cycle?.Council;
        if (council == null) { GD.Print("[ConcordDebug] No council."); return; }
        foreach (var inf in council.Informants)
        {
            if (inf.Role == ShadowVocab.RoleSaboteur)
            {
                var r = ShadowOps.SaboteurStrike(cycle, inf.Id, ShadowVocab.SabotageCorruption);
                GD.Print($"[ConcordDebug] SaboteurStrike: {r.Message}");
                return;
            }
        }
        GD.Print("[ConcordDebug] No saboteur planted (use Plant Saboteur, then ripen to access 2).");
    }

    /// <summary>First deep Cutout forges a favorable echo into its own court.</summary>
    public static void DebugForgeEcho()
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        var council = cycle?.Council;
        if (council == null) { GD.Print("[ConcordDebug] No council."); return; }
        foreach (var inf in council.Informants)
        {
            if (inf.Role == ShadowVocab.RoleCutout)
            {
                var r = ShadowOps.ForgeEcho(cycle, inf.Id, inf.KingdomId, positive: true);
                GD.Print($"[ConcordDebug] ForgeEcho: {r.Message}");
                return;
            }
        }
        GD.Print("[ConcordDebug] No cutout planted (turn a secret, then ripen to access 3).");
    }

    // ── E5 shadow-war verification ───────────────────────────────────────

    /// <summary>Force Marked to the Contracted-Against threshold so the next
    /// tick's roll can commission the Astrologer's contract.</summary>
    public static void DebugForceMarked(int value = 9)
    {
        var council = SaveManager.ActiveSave?.Cycle?.Council;
        if (council == null) { GD.Print("[ConcordDebug] No council."); return; }
        council.Marked = Mathf.Clamp(value, 0, ShadowVocab.MarkedMax);
        SaveManager.MarkDirty();
        GD.Print($"[ConcordDebug] Marked set to {council.Marked}.");
    }

    public static void DebugOutbid()
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        if (cycle?.Council == null) { GD.Print("[ConcordDebug] No council."); return; }
        var r = ShadowMarket.Outbid(cycle);
        GD.Print($"[ConcordDebug] Outbid: {r.Message}");
    }

    public static void DebugBuyExtraction() => CommissionInFirstCourt("extraction");

    /// <summary>Seize the first companion into a gaol so Extraction / rescue can
    /// be tested.</summary>
    public static void DebugImprisonEnvoy()
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        var council = cycle?.Council;
        if (council == null || council.Courts.Count == 0 || cycle.Companions.Count == 0)
        {
            GD.Print("[ConcordDebug] Need a court and a companion.");
            return;
        }
        string kingdomId = null;
        foreach (var kid in council.Courts.Keys) { kingdomId = kid; break; }
        string companionId = cycle.Companions[0].Id;
        bool ok = CouncilTick.SeizeEnvoyToGaol(cycle, kingdomId, companionId, cycle.Calendar.CurrentLunation);
        GD.Print(ok
            ? $"[ConcordDebug] Seized {cycle.Companions[0].Name} into a gaol at {kingdomId}."
            : "[ConcordDebug] Could not seize (already held, or no gaol tile).");
    }

    // ── E6 Tier C + spine verification ───────────────────────────────────

    /// <summary>Raise the Undercroft tier (0→3) so caps loosen and Assassination
    /// unlocks. Debug shim until the building is authored in the campus.</summary>
    public static void DebugUndercroftUp()
    {
        var save = SaveManager.ActiveSave;
        if (save == null) { GD.Print("[ConcordDebug] No save."); return; }
        BuildingSaveData b = null;
        foreach (var e in save.Buildings)
        {
            if (e.Id == ShadowVocab.BuildingUndercroft) { b = e; break; }
        }
        if (b == null)
        {
            b = new BuildingSaveData { Id = ShadowVocab.BuildingUndercroft, Name = "The Undercroft" };
            save.Buildings.Add(b);
        }
        b.Tier = Mathf.Min(3, b.Tier + 1);
        SaveManager.MarkDirty();
        GD.Print($"[ConcordDebug] Undercroft tier -> {b.Tier} " +
                 $"(informant cap {ShadowVocab.InformantCap(b.Tier)}, " +
                 $"contract cap {ShadowVocab.ContractCap(b.Tier)}).");
    }

    public static void DebugBuyAssassination()
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        var council = cycle?.Council;
        if (council == null || council.Courts.Count == 0)
        {
            GD.Print("[ConcordDebug] No courts.");
            return;
        }
        string kingdomId = null;
        CourtState court = null;
        foreach (var kv in council.Courts) { kingdomId = kv.Key; court = kv.Value; break; }
        string courtierId = court.Courtiers.Count > 0 ? court.Courtiers[0].Id : "";
        var r = ShadowMarket.CommissionAssassination(cycle, kingdomId, courtierId);
        GD.Print($"[ConcordDebug] Assassination: {r.Message}");
    }

    /// <summary>Exfiltrate the first informant, banking its renown.</summary>
    public static void DebugExfiltrate()
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        var council = cycle?.Council;
        if (council == null || council.Informants.Count == 0)
        {
            GD.Print("[ConcordDebug] No informant to exfiltrate.");
            return;
        }
        var r = ShadowOps.Exfiltrate(cycle, council.Informants[0].Id);
        GD.Print($"[ConcordDebug] Exfiltrate: {r.Message}");
    }
}
