using Godot;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// CityContractService.cs
//
// Purpose:        The Phase 3 "Quests" service — a visited city's
//                 CONTRACTS BOARD. Posts kingdom-scoped contracts
//                 (scout districts / purge enclaves / aid citizens),
//                 advances them from the city-explore verbs, and pays
//                 out on turn-in (gold + a Steward-routed echo).
// Layer:          Systems (strategic services)
// Collaborators:  CityContractState.cs (model), CityServicesHost.cs
//                 (board UI), StrategicView.cs (purge/aid hooks +
//                 completion toasts), WorldAtlas3D.cs (scout hook),
//                 CouncilEcho.cs (turn-in regard).
// Conventions:    Same CityId + lazy per-lunation refresh as
//                 CityMarketService; deterministic offer rolls so a
//                 reload mid-lunation shows the same board.
// ============================================================

/// <summary>Generation, progress and turn-in for city contracts boards.
/// Stateless; state lives in CycleState.CityContractBoards.</summary>
public static class CityContractService
{
    // ── Tuning (starting values — tune in playtest) ──────────────────────

    /// <summary>Posted offers: seat/capital 3, ordinary city 2 (towns have no board).</summary>
    public const int SeatOffers = 3;
    public const int CityOffers = 2;

    /// <summary>Gold per deed unit by kind, before the kingdom-tier factor
    /// (percent: tier 1 = 100, 2 = 140, 3+ = 180). Purge pays best — it is
    /// the only kind that risks a combat loss.</summary>
    public const int ScoutGoldPerUnit = 25;
    public const int PurgeGoldPerUnit = 70;
    public const int AidGoldPerUnit = 45;

    // ═════════════════════════════════════════════════════════════════════
    // Board stock
    // ═════════════════════════════════════════════════════════════════════

    public static CityContractBoardState GetOrRefresh(CycleState cycle, WorldSettlement city)
    {
        if (cycle == null || city == null) return null;

        string id = CityExploreService.CityId(city);
        var board = cycle.CityContractBoards.FirstOrDefault(b => b.CityId == id);
        if (board == null)
        {
            board = new CityContractBoardState { CityId = id };
            cycle.CityContractBoards.Add(board);
        }

        int now = cycle.Calendar.CurrentLunation;
        if (board.LastRefreshLunation == now)
            return board;

        // Reroll only the untaken paper: accepted contracts persist until
        // turned in; unaccepted offers are yesterday's postings and go.
        board.Offers.RemoveAll(c => !c.Accepted);

        int tier = KingdomTier(cycle, city.KingdomId);
        int want = (city.IsSeat ? SeatOffers : CityOffers) - board.Offers.Count;

        var rng = new RandomNumberGenerator();
        rng.Seed = Fnv1a(board.CityId) ^ (ulong)(now * 68041L + 13);

        for (int i = 0; i < want; i++)
        {
            int roll = rng.RandiRange(1, 100);
            string kind = roll <= 40 ? "scout" : roll <= 75 ? "purge" : "aid";
            int target = kind switch
            {
                "scout" => rng.RandiRange(3, 5),
                "purge" => rng.RandiRange(1, 2),
                _ => rng.RandiRange(1, 2),
            };
            int perUnit = kind switch
            {
                "scout" => ScoutGoldPerUnit,
                "purge" => PurgeGoldPerUnit,
                _ => AidGoldPerUnit,
            };
            int tierPct = tier switch { <= 1 => 100, 2 => 140, _ => 180 };
            board.Offers.Add(new CityContract
            {
                Id = $"{board.CityId}#{now}#{i}",
                Kind = kind,
                Target = target,
                GoldReward = perUnit * target * tierPct / 100,
                PostedLunation = now,
            });
        }

        board.LastRefreshLunation = now;
        SaveManager.MarkDirty();
        return board;
    }

    // ═════════════════════════════════════════════════════════════════════
    // Progress hooks (called from the city-explore verbs)
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>A district was scouted (revealed) in kingdomId's territory.</summary>
    public static List<string> NoteScout(CycleState cycle, string kingdomId)
        => Note(cycle, kingdomId, "scout");

    /// <summary>A district enclave was defeated in kingdomId's territory.</summary>
    public static List<string> NotePurge(CycleState cycle, string kingdomId)
        => Note(cycle, kingdomId, "purge");

    /// <summary>A district event was resolved in kingdomId's territory.</summary>
    public static List<string> NoteAid(CycleState cycle, string kingdomId)
        => Note(cycle, kingdomId, "aid");

    /// <summary>Advance every ACCEPTED, matching-kind contract posted by a board
    /// in this kingdom (boards embed their kingdom in the CityId prefix). Returns
    /// completion toast lines for the caller's toast manager — progress itself is
    /// silent (a tick per scouted tile would be noise).</summary>
    private static List<string> Note(CycleState cycle, string kingdomId, string kind)
    {
        var toasts = new List<string>();
        if (cycle?.CityContractBoards == null || string.IsNullOrEmpty(kingdomId))
            return toasts;

        foreach (var board in cycle.CityContractBoards)
        {
            if (BoardKingdom(board) != kingdomId) continue;
            foreach (var c in board.Offers)
            {
                if (!c.Accepted || c.Completed || c.Kind != kind) continue;
                c.Progress++;
                if (c.Progress >= c.Target)
                {
                    c.Completed = true;
                    toasts.Add($"Contract fulfilled: {Describe(cycle, board, c)} — collect at the board.");
                }
                SaveManager.MarkDirty();
            }
        }
        return toasts;
    }

    // ═════════════════════════════════════════════════════════════════════
    // Turn-in
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>Pay out a completed contract at its board and remove the posting:
    /// gold now, and word of dependable work reaching the kingdom's court
    /// (Steward-routed echo, minor positive). Returns the echo's toast line
    /// (null when the kingdom has no court).</summary>
    public static string TurnIn(CycleState cycle, CityContractBoardState board, CityContract c)
    {
        if (cycle == null || board == null || c == null || !c.Completed)
            return null;

        var save = SaveManager.ActiveSave;
        if (save != null) save.Gold += c.GoldReward;
        board.Offers.Remove(c);
        SaveManager.MarkDirty();

        return CouncilEcho.EmitDeed(cycle, BoardKingdom(board),
            CouncilEcho.ContractHonored, positive: true, isMajor: false);
    }

    // ═════════════════════════════════════════════════════════════════════
    // Display
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>One-line posting text, e.g. "Scout 4 districts across the
    /// settlements of the Boreal March".</summary>
    public static string Describe(CycleState cycle, CityContractBoardState board, CityContract c)
    {
        string realm = KingdomName(cycle, BoardKingdom(board));
        return c.Kind switch
        {
            "scout" => $"Scout {c.Target} district{Plural(c.Target)} across the settlements of {realm}",
            "purge" => $"Break {c.Target} hostile enclave{Plural(c.Target)} in the settlements of {realm}",
            _ => $"Aid the citizenry in {c.Target} district matter{Plural(c.Target)} across {realm}",
        };
    }

    private static string Plural(int n) => n == 1 ? "" : "s";

    /// <summary>The kingdom a board's contracts are scoped to — the CityId prefix
    /// ("{KingdomId}:{Cx},{Cy}", the CityExploreService convention).</summary>
    public static string BoardKingdom(CityContractBoardState board)
    {
        if (board == null) return "";
        int i = board.CityId.IndexOf(':');
        return i <= 0 ? "" : board.CityId.Substring(0, i);
    }

    private static int KingdomTier(CycleState cycle, string kingdomId)
        => cycle?.Kingdoms != null && !string.IsNullOrEmpty(kingdomId)
           && cycle.Kingdoms.TryGetValue(kingdomId, out var ks) ? ks.Tier : 1;

    private static string KingdomName(CycleState cycle, string kingdomId)
        => cycle?.Kingdoms != null && !string.IsNullOrEmpty(kingdomId)
           && cycle.Kingdoms.TryGetValue(kingdomId, out var ks)
           && !string.IsNullOrEmpty(ks.DisplayName) ? ks.DisplayName : "the kingdom";

    private static ulong Fnv1a(string s)
    {
        ulong h = 14695981039346656037UL;
        foreach (char ch in s ?? "")
        {
            h ^= ch;
            h *= 1099511628211UL;
        }
        return h;
    }
}
