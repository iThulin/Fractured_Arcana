using Godot;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

// ============================================================
// SaveManager.cs
//
// Purpose:        Save / load engine for the three-tier schema.
//                 Owns the active save (the in-memory envelope),
//                 writes TWO files per slot:
//                   slot_N_ledger.json — EternalLedger (tier 3,
//                     atomic write with .bak protection; the only
//                     permanent-loss vector in the game)
//                   slot_N_cycle.json  — CycleState (tier 2,
//                     replaced wholesale at cycle reset)
//                 v100 is a clean break: legacy slot_N.json saves
//                 are not migrated and are ignored (and removed
//                 by DeleteSlot).
// Layer:          System
// Collaborators:  GuildSaveData.cs (envelope + shims),
//                 EternalLedger.cs, CycleState.cs (the tiers),
//                 StarterDeckLoader.cs (seeds PlayerDeck),
//                 CompanionRoster.cs, CampusScreen.cs (callers)
// See:            open_world_refactor_v1.docx §10 — Save Schema
// ============================================================

/// <summary>
/// Process-wide save / load orchestrator for the three-tier schema.
/// Holds the active <see cref="GuildSaveData"/> envelope in memory and
/// persists its two halves to separate files per slot.
/// </summary>
public static class SaveManager
{
    private const string SAVE_DIR = "user://saves/";
    private const int MAX_SLOTS = 3;

    /// <summary>
    /// Schema version for BOTH tier files. v100 marks the three-tier era;
    /// anything older is a legacy save and is rejected, not migrated.
    /// Referenced by CycleState and EternalLedger field initializers.
    /// </summary>
    // v102 (2026-08-06): + CycleState.Convergence (ConvergenceState) — the finale
    // progress block. Deliberately NO migration: dev mode starts a new game per
    // test, so the stamp just invalidates older saves (ruling 2026-08-06). This
    // becomes a real migration only once saves are durable.
    public const int CURRENT_VERSION = 102;

    /// <summary>Canonical save serialization options — the single path every
    /// persisted structure travels. Public so round-trip assertions
    /// (CouncilSaveAssert, save-file-paranoia rule) exercise the REAL options,
    /// not a stand-in that could drift from these.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        IncludeFields = true,
    };

    // ── The active save (loaded into memory) ────────────────────────────
    public static GuildSaveData ActiveSave { get; private set; }
    public static int ActiveSlot { get; private set; } = -1;

    // ═══════════════════════════════════════════════════════════════════════
    // Save
    // ═══════════════════════════════════════════════════════════════════════
    private static bool _isDirty = false;

    public static void MarkDirty() => _isDirty = true;

    /// <summary>
    /// Save the active data to the active slot.
    /// Call this after every run completion and campus change.
    /// </summary>
    public static bool Save()
    {
        if (ActiveSave == null || ActiveSlot < 0)
        {
            GD.PrintErr("SaveManager: No active save to write.");
            return false;
        }
        _isDirty = false;

        // Settle any outstanding permanent progression (SchoolMastery, Regalia)
        // BEFORE the write, so grants are persisted in the same atomic save.
        // Deliberately a reconciliation sweep rather than ~8 scattered award
        // calls: it cannot be missed, it self-heals after a crash, and it pays
        // retroactively on saves that predate the system. Cheap and non-throwing.
        // See docs/progression_card_acquisition_v1.md §4, §6d.
        ProgressionSweep.Run(ActiveSave);

        ActiveSave.Ledger.LastPlayedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        return SaveToSlot(ActiveSlot, ActiveSave);
    }

    public static void SaveIfDirty()
    {
        if (_isDirty)
            Save();
    }

    /// <summary>
    /// Write both tier files for a slot. The ledger is written atomically
    /// with a .bak of the previous version; the cycle file is written via
    /// temp-and-rename (no .bak — a lost cycle is recoverable by design).
    /// </summary>
    public static bool SaveToSlot(int slot, GuildSaveData data)
    {
        if (slot < 0 || slot >= MAX_SLOTS)
        {
            GD.PrintErr($"SaveManager: Invalid slot {slot}");
            return false;
        }

        EnsureSaveDirectory();

        // Keep both files stamped with the same version.
        data.Ledger.SaveVersion = CURRENT_VERSION;
        data.Cycle.SaveVersion = CURRENT_VERSION;

        string ledgerJson = JsonSerializer.Serialize(data.Ledger, JsonOptions);
        string cycleJson = JsonSerializer.Serialize(data.Cycle, JsonOptions);

        bool ledgerOk = WriteFileSafe(GetLedgerPath(slot), ledgerJson, keepBackup: true,
                                      verify: VerifyLedgerJson);
        bool cycleOk = WriteFileSafe(GetCyclePath(slot), cycleJson, keepBackup: false,
                                     verify: VerifyCycleJson);

        if (ledgerOk && cycleOk)
            GD.Print($"SaveManager: Saved slot {slot} " +
                     $"(ledger {ledgerJson.Length} chars, cycle {cycleJson.Length} chars)");
        else
            GD.PrintErr($"SaveManager: Save to slot {slot} incomplete " +
                        $"(ledger={ledgerOk}, cycle={cycleOk})");

        return ledgerOk && cycleOk;
    }

    /// <summary>
    /// Temp-write → verify → swap. If keepBackup, the previous file is
    /// preserved as {path}.bak before the swap. Returns true on success;
    /// on any failure the existing file is left untouched.
    /// </summary>
    private static bool WriteFileSafe(string path, string contents, bool keepBackup,
                                      Func<string, bool> verify)
    {
        string tmpPath = path + ".tmp";
        string bakPath = path + ".bak";

        // 1) Write the temp file.
        try
        {
            using var file = FileAccess.Open(tmpPath, FileAccess.ModeFlags.Write);
            if (file == null)
            {
                GD.PrintErr($"SaveManager: Could not open {tmpPath} for writing. " +
                            $"Error: {FileAccess.GetOpenError()}");
                return false;
            }
            file.StoreString(contents);
        }
        catch (Exception e)
        {
            GD.PrintErr($"SaveManager: Temp write failed for {path}: {e.Message}");
            return false;
        }

        // 2) Read the temp file back and verify it parses.
        try
        {
            using var check = FileAccess.Open(tmpPath, FileAccess.ModeFlags.Read);
            if (check == null || !verify(check.GetAsText()))
            {
                GD.PrintErr($"SaveManager: Verification failed for {tmpPath} — " +
                            "existing file left untouched.");
                return false;
            }
        }
        catch (Exception e)
        {
            GD.PrintErr($"SaveManager: Verification failed for {tmpPath}: {e.Message}");
            return false;
        }

        // 3) Swap: existing → .bak (or removed), tmp → final.
        string gPath = ProjectSettings.GlobalizePath(path);
        string gTmp = ProjectSettings.GlobalizePath(tmpPath);
        string gBak = ProjectSettings.GlobalizePath(bakPath);

        if (FileAccess.FileExists(path))
        {
            if (keepBackup)
            {
                if (FileAccess.FileExists(bakPath))
                    DirAccess.RemoveAbsolute(gBak);
                if (DirAccess.RenameAbsolute(gPath, gBak) != Error.Ok)
                {
                    GD.PrintErr($"SaveManager: Could not back up {path} — aborting swap.");
                    return false;
                }
            }
            else
            {
                DirAccess.RemoveAbsolute(gPath);
            }
        }

        if (DirAccess.RenameAbsolute(gTmp, gPath) != Error.Ok)
        {
            GD.PrintErr($"SaveManager: Final rename failed for {path}.");
            // Best effort: restore the backup so the slot isn't left empty.
            if (keepBackup && FileAccess.FileExists(bakPath))
                DirAccess.RenameAbsolute(gBak, gPath);
            return false;
        }

        return true;
    }

    private static bool VerifyLedgerJson(string json)
    {
        try
        { return JsonSerializer.Deserialize<EternalLedger>(json, JsonOptions) != null; }
        catch { return false; }
    }

    private static bool VerifyCycleJson(string json)
    {
        try
        { return JsonSerializer.Deserialize<CycleState>(json, JsonOptions) != null; }
        catch { return false; }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Load
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Load a save slot into ActiveSave. Returns true if successful.
    /// </summary>
    public static bool Load(int slot)
    {
        var data = LoadFromSlot(slot);
        if (data == null)
            return false;

        ActiveSave = data;
        ActiveSlot = slot;

        // Draft-pool breadth on load. SeedUnlockedPool otherwise only runs from
        // SeedStarterDeck, i.e. on new game and on a new cycle — so a save loaded
        // and played normally would draft from whatever UnlockedCardBlueprintIds
        // happened to accumulate, which on a pre-gate save is only the handful of
        // cards that were previously drafted. Idempotent, so this costs nothing
        // on a save that already has its pool.
        StarterDeckLoader.SeedUnlockedPool(data);

        // A guild that was already studying a discipline before the faculty gate
        // existed keeps it, permanently — not just for as long as it stays in it.
        DeclarationService.GrandfatherCurrentSchool(data);

        // Knowledge that used to die with the timeline, reconciled onto the loom.
        // Both are grandfather-first: an existing save's spells and per-copy card
        // mastery are absorbed BEFORE anything reseeds, so nothing accumulated
        // under the old rules is lost. Both are idempotent.
        SpellKnowledgeService.Sync(data);
        CardMasteryService.AbsorbOwnedCopies(data);

        // Settle any permanent progression the save is owed. Retroactive by
        // design: a guild that allied three archmagi before this system existed
        // gets paid the moment it loads.
        ProgressionSweep.Run(data);

        // Self-heal any research commission that finished but lost its settlement
        // to a crash between the lunation tick and the save (§8 pity-timer). The
        // tick is the normal settle path; this only pays a completed-but-unsettled
        // one. Idempotent — a no-op on the common case.
        CardCommissionService.Reconcile(data);

        GD.Print($"SaveManager: Loaded slot {slot} " +
                 $"(v{data.Ledger.SaveVersion}, guild: {data.Ledger.GuildName}, " +
                 $"cycle {data.Cycle.CycleNumber})");
        return true;
    }

    /// <summary>Load the most-recently-saved slot into ActiveSave when none is active,
    /// so the game and the combat debugger always have a save to work with. No-op if a
    /// save is already loaded — a slot the player explicitly picked always wins.</summary>
    public static bool AutoLoadLast()
    {
        if (ActiveSave != null)
            return true;

        int best = -1;
        ulong bestTime = 0;
        for (int i = 0; i < MAX_SLOTS; i++)
        {
            ulong t = 0;
            string ledger = GetLedgerPath(i);
            string cycle = GetCyclePath(i);
            if (FileAccess.FileExists(ledger))
                t = System.Math.Max(t, FileAccess.GetModifiedTime(ledger));
            if (FileAccess.FileExists(cycle))
                t = System.Math.Max(t, FileAccess.GetModifiedTime(cycle));
            if (t > 0 && (best < 0 || t > bestTime))
            { best = i; bestTime = t; }
        }

        if (best < 0)
        {
            GD.Print("SaveManager: AutoLoadLast — no saves found.");
            return false;
        }
        bool ok = Load(best);
        GD.Print($"SaveManager: AutoLoadLast → slot {best} ({(ok ? "loaded" : "failed")}).");
        return ok;
    }

    /// <summary>
    /// Assemble a GuildSaveData envelope from a slot's two files.
    /// The ledger is required (with .bak fallback). A missing cycle file
    /// is a legitimate between-cycles state — a fresh CycleState is
    /// created (school selection happens at cycle start, not here).
    /// </summary>
    public static GuildSaveData LoadFromSlot(int slot)
    {
        if (slot < 0 || slot >= MAX_SLOTS)
            return null;

        // ── Tier 3: the ledger (required) ───────────────────────────────
        var ledger = ReadJson<EternalLedger>(GetLedgerPath(slot));
        if (ledger == null)
        {
            string bak = GetLedgerPath(slot) + ".bak";
            ledger = ReadJson<EternalLedger>(bak);
            if (ledger != null)
                GD.PrintErr($"SaveManager: Ledger for slot {slot} was unreadable — " +
                            "RECOVERED FROM BACKUP. Last session's ledger changes may be lost.");
        }

        if (ledger == null)
            return null; // empty slot (or pre-v100 legacy — ignored by design)

        if (ledger.SaveVersion != CURRENT_VERSION)
        {
            GD.PrintErr($"SaveManager: Slot {slot} ledger is v{ledger.SaveVersion}, " +
                        $"expected v{CURRENT_VERSION}. Incompatible save — not loaded.");
            return null;
        }

        // ── Lazy migration: v100 saves made before CampusMap existed load with
        // an empty Tiles list (the JSON simply has no campusMap key). Treat that
        // the same way a missing cycle file is treated above — backfill rather
        // than fail the load. Never runs again once a real layout is saved.
        if (ledger.CampusMap == null || ledger.CampusMap.Tiles.Count == 0)
        {
            GD.Print($"SaveManager: Slot {slot} ledger predates the campus map — " +
                     "generating a default layout.");
            ledger.CampusMap = CampusMapSaveData.GenerateDefault();
        }
        else if (ledger.CampusMap.Districts == null || ledger.CampusMap.Districts.Count == 0)
        {
            // District-campus migration (Phase 2, Stage 3): a pre-district save has Tiles
            // (a solid disc) but no Districts. Regenerate as districts and UNPLACE buildings
            // — their old disc coords don't map to the flower layout — so the player re-sites
            // them on the new grounds. Dev saves only.
            GD.Print($"SaveManager: Slot {slot} campus predates districts — regenerating as " +
                     "districts; existing buildings are unplaced.");
            ledger.CampusMap = CampusMapSaveData.GenerateDefault();
            if (ledger.Buildings != null)
                foreach (var b in ledger.Buildings)
                    if (b != null)
                        b.IsPlaced = false;
        }
        else if (ledger.CampusMap.LatticeVersion < 3)
        {
            // Flower-lattice migration (Phase 2): districts ARE strategic map tiles; the
            // fine lattice is the 1/3-scale unrotated cut (whole 7-flower per district,
            // vertex cells as 3-way bonus corners; 3-district founding). Regenerate the map
            // (preserving the resolved dock type), then unplace only the buildings stranded
            // off the new grid.
            GD.Print($"SaveManager: Slot {slot} campus predates the /3 district lattice — " +
                     "regenerating; stranded buildings are unplaced.");
            string dock = ledger.CampusMap.EntryDockType;
            ledger.CampusMap = CampusMapSaveData.GenerateDefault();
            ledger.CampusMap.EntryDockType = dock;
            var validTiles = new System.Collections.Generic.HashSet<(int, int)>();
            foreach (var t in ledger.CampusMap.Tiles)
                validTiles.Add((t.Q, t.R));
            if (ledger.Buildings != null)
                foreach (var b in ledger.Buildings)
                    if (b != null && b.IsPlaced && !validTiles.Contains((b.Q, b.R)))
                        b.IsPlaced = false;
        }

        // ── Lazy migration: saves founded before start-scenarios existed have no
        // FoundingScenario. Backfill the Standard default so difficulty reads
        // identically to shipping (all levers 1.0, no start hint). Same additive,
        // no-version-bump pattern as the CampusMap backfill above — a version bump
        // would REJECT the save outright (see the SaveVersion guard).
        if (ledger.FoundingScenario == null)
            ledger.FoundingScenario = StartScenarioLoader.Default();

        // ── Tier 2: the cycle (optional — between-cycles is valid) ──────
        var cycle = ReadJson<CycleState>(GetCyclePath(slot));
        if (cycle == null)
        {
            GD.Print($"SaveManager: Slot {slot} has no cycle file — between cycles. " +
                     "Creating a fresh CycleState (school unselected).");
            // SelectedSchool is set EXPLICITLY: CycleState's field initializer is
            // "Elementalist", which made this 'school unselected' state silently
            // Elementalist — and DeclarationService's grandfather clause then
            // reported Elementalist as a declared discipline on any between-cycles
            // load. Adept is the honest value: it is where every guild stands
            // when no discipline is in play (A1), and it is always declared anyway.
            cycle = new CycleState
            {
                CycleNumber = ledger.LoopHistory.Count + 1,
                SelectedSchool = DeclarationService.StartingSchool,
            };
        }
        else if (cycle.SaveVersion != CURRENT_VERSION)
        {
            GD.PrintErr($"SaveManager: Slot {slot} cycle is v{cycle.SaveVersion}, " +
                        $"expected v{CURRENT_VERSION}. Incompatible save — not loaded.");
            return null;
        }

        return new GuildSaveData { Ledger = ledger, Cycle = cycle };
    }

    private static T ReadJson<T>(string path) where T : class
    {
        if (!FileAccess.FileExists(path))
            return null;

        try
        {
            using var file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
            if (file == null)
                return null;
            return JsonSerializer.Deserialize<T>(file.GetAsText(), JsonOptions);
        }
        catch (Exception e)
        {
            GD.PrintErr($"SaveManager: Read failed for {path}: {e.Message}");
            return null;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // New game / new cycle
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Create a fresh guild (new ledger AND first cycle) in the given slot
    /// and make it active. <paramref name="school"/> is required so the
    /// starter deck can be seeded immediately.
    /// </summary>
    public static GuildSaveData NewGame(int slot, string guildName = "New Guild",
                                        string school = "Elementalist")
    {
        string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        var data = new GuildSaveData
        {
            Ledger = new EternalLedger
            {
                GuildName = guildName,
                CreatedAt = now,
                LastPlayedAt = now,
                CampusMap = CampusMapSaveData.GenerateDefault(),
            },
            Cycle = new CycleState
            {
                CycleNumber = 1,
                SelectedSchool = school,
                // Base founding gold. The founding scenario's StartingGold is applied
                // as a delta on top (NewGameScreen.OnConfirmPressed), floored at 0 —
                // so a positive base lets negative scenario deltas actually bite
                // (e.g. Brutal −200 → 0 cushion) instead of being inert. Tune freely.
                Gold = 200,
            },
        };

        SeedDeckForSchool(data, school);

        ActiveSave = data;
        ActiveSlot = slot;
        SaveToSlot(slot, data);

        GD.Print($"SaveManager: New guild in slot {slot} (school: {school})");
        return data;
    }

    /// <summary>
    /// End the current cycle and begin the next timeline. Archives a
    /// LoopRecord into the eternal ledger, replaces the CycleState
    /// wholesale, and seeds the new school's starter deck.
    /// Phase 5 expands this (trace-back eclipse scheduling on losses,
    /// renown anchoring prompts, Kassian adaptation inputs).
    /// </summary>
    /// <param name="school">School for the new cycle (one cycle, one school).</param>
    /// <param name="outcome">"Victory", "ConvergenceDefeat", "CorruptionLoss", or "Abandoned".</param>
    /// <param name="resolutionPath">"Restoration", "Harness", "Synthesis", or "" for non-victories.</param>
    public static GuildSaveData BeginNewCycle(string school, string outcome,
                                              string resolutionPath = "")
    {
        if (ActiveSave == null || ActiveSlot < 0)
        {
            GD.PrintErr("SaveManager: No active save — cannot begin a new cycle.");
            return null;
        }

        var old = ActiveSave.Cycle;

        // ── Settle progression BEFORE the timeline is unmade ─────────────
        // ProgressionSweep reads Cycle.Campaign.Dispositions and Cycle.Companions,
        // both of which are destroyed by the CycleState swap below. The Save() at
        // the end of this method would sweep an already-empty cycle, so an
        // archmage allied (or a companion arc finished) in the final lunation,
        // with no intervening save, would never be paid. Sweep here instead.
        ProgressionSweep.Run(ActiveSave);

        // Capture this timeline's knowledge BEFORE it is unmade. Both of these read
        // cycle state the swap below destroys: the deck's per-copy cast counts and
        // upgrade tiers, and the Grimoire's spell list. Miss this and a player loses
        // everything they learned in the cycle they learned it in.
        CardMasteryService.AbsorbOwnedCopies(ActiveSave);
        SpellKnowledgeService.Sync(ActiveSave);

        // Completing a cycle is the single largest SchoolMastery award, and this
        // is the only place a cycle ends. Awarded here rather than in the sweep
        // because it is an event, not a reconcilable state — LoopHistory already
        // records it and re-deriving it from there would double-pay on every save.
        // Outcome-blind by design: a lost timeline still taught you the school.
        //
        // Guarded by a paid flag anyway. The picker's school buttons stay live
        // for the rest of the frame after they fire (QueueFree and the scene
        // change are both deferred), so a double-click can re-enter this method;
        // and the TODO below plans to route more callers through it. Keyed to the
        // ENDING cycle number so it also clears with the prog_paid_ debug sweep.
        string paidCycleFlag = $"prog_paid_cycle_{old.CycleNumber}";
        if (!ActiveSave.Ledger.MetaNarrativeFlags.Contains(paidCycleFlag))
        {
            SchoolMasteryService.Award(ActiveSave, old.SelectedSchool,
                SchoolMasteryService.PointsCycleCompleted,
                $"cycle {old.CycleNumber} completed ({outcome})");
            ActiveSave.Ledger.MetaNarrativeFlags.Add(paidCycleFlag);
        }

        // ── Archive the ended timeline into the loom ────────────────────
        var record = new LoopRecord
        {
            CycleNumber = old.CycleNumber,
            School = old.SelectedSchool,
            Outcome = outcome,
            ResolutionPath = resolutionPath,
            LunationsElapsed = old.Calendar.CurrentLunation,
            RunsCompleted = old.TotalRuns,
        };
        foreach (var kvp in old.Campaign.Dispositions)
            record.FinalDispositions[kvp.Key] = kvp.Value.ToString();

        ActiveSave.Ledger.LoopHistory.Add(record);

        // ── Archive unfinished timeline quests (quest spec §7) ─────────
        // Before the CycleState is replaced, snapshot every Timeline quest
        // that was active (unlocked, not complete) into UnfinishedBusiness.
        // This is the "cost of every reset, itemized." Skipped on Continue
        // (ContinueCampaign never calls this method).
        ArchiveUnfinishedQuests(old);

        // ── A new timeline ──────────────────────────────────────────────
        ActiveSave.Cycle = new CycleState
        {
            CycleNumber = old.CycleNumber + 1,
            SelectedSchool = school,
        };

        SeedDeckForSchool(ActiveSave, school);

        Save();
        GD.Print($"SaveManager: Cycle {old.CycleNumber} archived ({outcome}). " +
                 $"Cycle {ActiveSave.Cycle.CycleNumber} begun (school: {school}).");
        return ActiveSave;
    }

    /// <summary>
    /// CANONICAL: claude/progression_persistence_model_v1.md §4 — the "Continue"
    /// (press-your-luck) transition. Unlike <see cref="BeginNewCycle"/>, which
    /// unmakes the timeline for a fresh gen-1 world, this KEEPS the current timeline
    /// (world, corruption, staging, deck, items, companions) and pushes it into the
    /// next CampaignYear: the calendar clock resets and the world hardens (one
    /// escalation pass). Nothing is archived and the deck is NOT reseeded. Defeat
    /// and Bank both route through BeginNewCycle; Continue is the only path that
    /// preserves the timeline.
    /// </summary>
    // Victory-gating: RESOLVED by R-F2 (docs/convergence_finale_spec_v1.md §0, §3),
    // implemented in I1 as a HYBRID, and gated by the CALLER rather than here:
    //   • while seats remain unresolved, the Conjunction is a pure deadline and
    //     surviving to it still earns Continue — StrategicView's unresolved branch;
    //   • once every seat is resolved, the Conjunction IS the Convergence, and
    //     Continue is offered from exactly one place: the post-VICTORY beat
    //     (StrategicView.ShowConvergenceOutcome). A defeat has no Continue button
    //     and routes to the campus, where BeginNextCycle archives
    //     "ConvergenceDefeat" and BeginNewCycle unmakes the timeline.
    // This method stays outcome-agnostic on purpose: it is the "keep the timeline"
    // mechanic, not the adjudicator of who has earned it.
    public static GuildSaveData ContinueCampaign()
    {
        if (ActiveSave == null || ActiveSlot < 0)
        {
            GD.PrintErr("SaveManager: No active save — cannot continue the campaign.");
            return null;
        }

        var cycle = ActiveSave.Cycle;

        // The timeline persists. Advance the year and reset only the clock.
        cycle.CampaignYear += 1;
        cycle.Calendar = new CalendarState();   // lunation 1, phase 0, no eclipses
        cycle.PendingStraggleLunations = 0;     // a new year owes no straggle debt

        // The world hardens (the forcing clock — progression doc §6).
        CampaignEscalation.Apply(cycle);

        Save();
        GD.Print($"SaveManager: Timeline held into Year {cycle.CampaignYear} " +
                 $"(cycle {cycle.CycleNumber}). The world hardens.");
        return ActiveSave;
    }

    /// <summary>Archive active Timeline quests into UnfinishedBusiness before
    /// the CycleState is replaced. Called only from <see cref="BeginNewCycle"/>
    /// — <see cref="ContinueCampaign"/> skips this because the timeline persists.</summary>
    private static void ArchiveUnfinishedQuests(CycleState endingCycle)
    {
        if (ActiveSave?.Ledger == null || endingCycle == null)
            return;

        var quests = QuestLoader.LoadAll();
        foreach (var q in quests)
        {
            // Only archive Timeline quests (Eternal quests survive the wipe)
            if (q.EffectiveLayer != "Timeline")
                continue;

            var status = QuestTracker.StatusOf(q, ActiveSave);
            if (status != QuestStatus.Active)
                continue; // skip locked and completed

            int objDone = 0, objTotal = q.Objectives?.Count ?? 0;
            if (q.Objectives != null)
                foreach (var o in q.Objectives)
                    if (QuestTracker.ObjectiveDone(o, ActiveSave))
                        objDone++;

            ActiveSave.Ledger.UnfinishedBusiness.Add(new UnfinishedQuestRecord
            {
                QuestId = q.Id,
                Title = q.Title,
                Summary = q.Summary,
                ObjectivesDone = objDone,
                ObjectivesTotal = objTotal,
                CycleNumber = endingCycle.CycleNumber,
                CampaignYear = endingCycle.CampaignYear,
                School = endingCycle.SelectedSchool ?? "",
            });
        }
    }

    private static void SeedDeckForSchool(GuildSaveData data, string school)
    {
        // CardDatabase must be loaded before this is called.
        if (Enum.TryParse<CardSchool>(school, ignoreCase: true, out var cardSchool))
            StarterDeckLoader.SeedStarterDeck(data, cardSchool);
        else
            GD.PrintErr($"SaveManager: Unknown school '{school}' — PlayerDeck not seeded.");

        // Regalia ride ON TOP of the 10-card starter floor, never in place of it —
        // so this must run after SeedStarterDeck, which bails early if the deck is
        // already populated. The ONE sanctioned exception to the reseed
        // (docs/progression_card_acquisition_v1.md §6; amends
        // progression_persistence_model_v1.md §5).
        RegaliaService.SeedCarriedIntoDeck(data);

        // Re-seed the fresh Grimoire from the loom. A new timeline no longer starts
        // knowing nothing — knowledge crossed with you; only preparation, scrolls
        // and Essence reset. Runs here because the new CycleState (and its empty
        // Grimoire) exists by this point.
        SpellKnowledgeService.Sync(data);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Slot info (for the slot selection UI)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Get summary info for all save slots. Used by the campus/menu UI.
    /// </summary>
    public static List<SlotInfo> GetAllSlotInfo()
    {
        var slots = new List<SlotInfo>();

        for (int i = 0; i < MAX_SLOTS; i++)
        {
            var info = new SlotInfo { Slot = i, IsEmpty = true };

            var data = LoadFromSlot(i);
            if (data != null)
            {
                info.IsEmpty = false;
                info.GuildName = data.Ledger.GuildName;
                info.School = data.Cycle.SelectedSchool;
                info.Gold = data.Cycle.Gold;
                info.TotalRuns = data.Cycle.TotalRuns;
                info.CycleNumber = data.Cycle.CycleNumber;
                info.LastPlayed = data.Ledger.LastPlayedAt;
            }

            slots.Add(info);
        }

        return slots;
    }

    /// <summary>
    /// Delete a save slot — both tier files, their backups/temps, and any
    /// legacy pre-v100 single-file save occupying the slot name.
    /// </summary>
    public static void DeleteSlot(int slot)
    {
        string[] paths =
        {
            GetCyclePath(slot),
            GetCyclePath(slot) + ".tmp",
            GetLedgerPath(slot),
            GetLedgerPath(slot) + ".bak",
            GetLedgerPath(slot) + ".tmp",
            GetLegacyPath(slot),
        };

        foreach (var path in paths)
        {
            if (FileAccess.FileExists(path))
                DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(path));
        }

        GD.Print($"SaveManager: Deleted slot {slot}");

        if (ActiveSlot == slot)
        {
            ActiveSave = null;
            ActiveSlot = -1;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════════

    private static string GetCyclePath(int slot) => $"{SAVE_DIR}slot_{slot}_cycle.json";
    private static string GetLedgerPath(int slot) => $"{SAVE_DIR}slot_{slot}_ledger.json";
    private static string GetLegacyPath(int slot) => $"{SAVE_DIR}slot_{slot}.json";

    private static void EnsureSaveDirectory()
    {
        if (!DirAccess.DirExistsAbsolute(ProjectSettings.GlobalizePath(SAVE_DIR)))
        {
            DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath(SAVE_DIR));
        }
    }
}

/// <summary>
/// Summary info for displaying save slots in the UI.
/// </summary>
public class SlotInfo
{
    public int Slot;
    public bool IsEmpty;
    public string GuildName = "";
    public string School = "";
    public int Gold;
    public int TotalRuns;
    public int CycleNumber;
    public string LastPlayed = "";
}
