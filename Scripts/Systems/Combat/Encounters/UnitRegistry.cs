using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

// ============================================================
// UnitRegistry.cs  (U1 · U2)
//
// Purpose:        Process-wide loader + registry for UnitDefinitions
//                 (Data/Units/*.json), mirroring ItemDatabase's idiom.
//                 The single place combat resolves a unit's stats,
//                 keyed by id. Holds the legacy-name alias table
//                 ("Soldier" → generic_soldier) so authored encounter
//                 JSON that still names archetypes keeps resolving.
//                 U2: the EnemyArchetype enum is deleted; all lookups
//                 are string-keyed. Resolution order for authored
//                 tokens: exact unit id → legacy alias → fail loudly
//                 (units doc §6 step 2).
// Layer:          Loader
// Collaborators:  UnitDefinition.cs, EncounterPoolLoader.cs (token
//                 resolution), CombatManager.cs (spawn),
//                 CombatDebugLauncher.cs (AllIds roster).
// See:            build_order_v3 §4 (U2)
// ============================================================

/// <summary>Loads and caches UnitDefinitions. Lazy load on first access;
/// robust to missing data (logs, returns a non-null default so combat never
/// null-refs). Legacy archetype names resolve through the alias table.</summary>
public static class UnitRegistry
{
    private const string UNITS_DIR = "res://Data/Units/";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        IncludeFields = true,
        PropertyNameCaseInsensitive = true,
    };

    // Legacy archetype name -> canonical unit id. Keeps every existing region
    // and archmage pool JSON ("archetype": "Soldier") working unmodified.
    private static readonly Dictionary<string, string> LegacyAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Soldier",  "generic_soldier" },
        { "Brute",    "generic_brute" },
        { "Defender", "generic_defender" },
        { "Ranger",   "generic_ranger" },
        { "Wizard",   "generic_wizard" },
    };

    private static readonly Dictionary<string, UnitDefinition> _cache = new();
    private static readonly UnitDefinition _fallback = new() { Id = "fallback", ThreatLabel = "Unknown" };
    private static bool _loaded = false;

    public static void LoadAll()
    {
        if (_loaded) return;
        _loaded = true;
        _cache.Clear();

        if (!DirAccess.DirExistsAbsolute(ProjectSettings.GlobalizePath(UNITS_DIR)))
        {
            GD.PrintErr($"UnitRegistry: No units directory at {UNITS_DIR}");
            return;
        }

        using var dir = DirAccess.Open(UNITS_DIR);
        if (dir == null) return;

        dir.ListDirBegin();
        string filename = dir.GetNext();
        while (filename != "")
        {
            if (!dir.CurrentIsDir() && filename.EndsWith(".json"))
            {
                LoadFile($"{UNITS_DIR}{filename}");
            }
            filename = dir.GetNext();
        }
        dir.ListDirEnd();

        GD.Print($"UnitRegistry: Loaded {_cache.Count} unit definition(s).");
    }

    private static void LoadFile(string path)
    {
        try
        {
            using var file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
            if (file == null) return;

            var def = JsonSerializer.Deserialize<UnitDefinition>(file.GetAsText(), JsonOptions);
            if (def == null || string.IsNullOrEmpty(def.Id)) return;

            _cache[def.Id] = def;
        }
        catch (Exception e)
        {
            GD.PrintErr($"UnitRegistry: Error loading {path}: {e.Message}");
        }
    }

    /// <summary>Definition by id, or a logged non-null fallback if absent.</summary>
    public static UnitDefinition Get(string id)
    {
        LoadAll();
        if (_cache.TryGetValue(id, out var def)) return def;
        GD.PrintErr($"UnitRegistry: No unit definition '{id}' — using fallback.");
        return _fallback;
    }

    /// <summary>All loaded unit ids, sorted — the debug launcher's roster.</summary>
    public static IReadOnlyList<string> AllIds
    {
        get { LoadAll(); return _cache.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList(); }
    }

    /// <summary>Resolve an authored token — an exact unit id ("generic_soldier",
    /// "conductor_honored_dead") OR a legacy archetype name ("Soldier") — to a
    /// canonical unit id. Resolution order per units doc §6 step 2: exact id
    /// first, legacy alias second, false (caller names the offending pool) last.</summary>
    public static bool TryResolveId(string token, out string unitId)
    {
        LoadAll();
        unitId = "";
        if (string.IsNullOrWhiteSpace(token))
            return false;

        if (_cache.ContainsKey(token))
        {
            unitId = token;
            return true;
        }

        if (LegacyAliases.TryGetValue(token.Trim(), out var aliased) && _cache.ContainsKey(aliased))
        {
            unitId = aliased;
            return true;
        }

        return false;
    }

    // ── Verification (U1 parity + round-trip; U2 adds tags + key catalog) ───

    /// <summary>Assert every generic_* def loaded from JSON matches the stats the
    /// old EnemyArchetypeData hardcoded (parity), that a UnitDefinition survives a
    /// JSON round-trip (including BehaviorTags), that every loaded def's
    /// BehaviorKey is in the dispatcher's catalog, and that legacy alias
    /// resolution works. Prints PASS/FAIL; PushErrors on failure. The expected
    /// table below is the test oracle — the ONLY place the old numbers still
    /// live. Wired to the CampusScreen debug panel.</summary>
    public static bool AssertParityAndRoundTrip()
    {
        LoadAll();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== UNIT REGISTRY — PARITY + ROUND-TRIP ===");
        bool ok = true;

        // id, HP, spd, armor, range, dmg, prefDist, label, r, g, b
        (string id, int hp, int spd, int arm, int rng, int dmg, int pd, string lbl, float r, float g, float b)[] expected =
        {
            ("generic_soldier",  20, 2, 0, 1, 5, 1, "Soldier",  1.0f, 0.25f, 0.25f),
            ("generic_brute",    40, 1, 0, 1, 8, 1, "Brute",    0.8f, 0.2f,  0.9f),
            ("generic_defender", 25, 1, 5, 1, 4, 1, "Defender", 0.2f, 0.5f,  0.9f),
            ("generic_ranger",   15, 2, 0, 3, 4, 3, "Ranger",   0.2f, 0.8f,  0.3f),
            ("generic_wizard",   12, 1, 0, 5, 9, 4, "Wizard",   0.9f, 0.9f,  0.1f),
        };

        foreach (var e in expected)
        {
            var d = Get(e.id);
            bool m = d.Id == e.id && d.MaxHealth == e.hp && d.BaseSpeed == e.spd &&
                     d.Armor == e.arm && d.AttackRange == e.rng && d.AttackDamage == e.dmg &&
                     d.PreferredDistance == e.pd && d.ThreatLabel == e.lbl &&
                     Mathf.IsEqualApprox(d.ColorR, e.r) && Mathf.IsEqualApprox(d.ColorG, e.g) &&
                     Mathf.IsEqualApprox(d.ColorB, e.b);
            sb.AppendLine(m ? $"  {e.id}: PARITY OK" : $"  {e.id}: PARITY FAIL (json drifted from spec)");
            ok &= m;
        }

        // U2: every loaded def's BehaviorKey must be in the dispatcher catalog —
        // a typo in an authored JSON should fail HERE, not silently soldier-fallback
        // in a fight. Keep in sync with CombatManager.EnemyIntents' handler map.
        var knownKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "melee_advance", "melee_target_highest_hp", "hold_until_near",
            "ranged_kite", "ranged_charge", "melee_hunt_wounded",
            // tile_interaction §7: gust elite. Telegraphed, dodgeable, deals no direct
            // damage — its threat is force-moving the player onto hazards. Legal as a
            // BehaviorKey (shoves as its whole identity) or a cycle beat.
            "shove",
            // City siege: interruptible teleport assault (CombatManager.WarpChanneler).
            // Channels a telegraphed rift two activations, collapses on ANY damage,
            // lands beside its target ignoring walls. Melee fallback when close.
            "warp_channeler",
        };
        // U3a: keys legal ONLY inside an IntentCycle. hold_ground braces and never
        // advances, so a unit whose base routine were hold_ground would stand still
        // for the whole fight — legal as a scripted beat, never as a BehaviorKey.
        var cycleOnlyKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "hold_ground",
            // tile_interaction §7: telegraphed ground imbue. Cycle-only on purpose —
            // an imbue-every-turn personality is degenerate hazard spam, so it must be
            // composed into a rotation with a real advancing beat (a looping cycle of
            // only non-advancing beats is rejected below). At runtime Imbue advances
            // the index normally (its Kind != Channel).
            "imbue",
        };
        var knownTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "pack", "bulwark", "charge", "scout", "immobile",
            // tile_interaction §7 hazard-avoidance temperament (read by Unit.HazardCaution):
            "reckless", "cautious",
        };
        // U3: ability audit — keys against the trigger-bus handler map, triggers
        // against the bounded taxonomy (units doc §5), hard cap two per unit.
        var knownAbilityKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "requiem", "deathburst",
            "retaliate", "regrowth", "mode_shift",          // U3c — stack effects
            "chitin", "veil",                                // U3c — auras (see auraKeys)
            "ritual", "summon_cadence", "field_repair",      // U3d — stack effects
            "bodyguard",                                     // U3d — radius aura
            "redact", "overdraw_ward",                       // U3e — stack effects
            "tithe_aura", "school_grudge",                   // U3e — auras (see auraKeys)
            "action_tax", "binding_geas", "hand_cap",        // U3e — auras (see auraKeys)
            "debug_echo",   // U3b verification scaffold — remove with debug_trigger_probe
        };
        // U3e (spec §8, U3e exit criterion): Axis A attacks the player's hand, mana
        // and action economy — the same resources the card draw already randomises.
        // Elite-gating is the mitigation the spec names, so the gate is enforced HERE
        // rather than trusted to pool authoring discipline.
        //
        // NOTE FOR §7a: three LINE units in the target roster carry Axis A keys —
        // `censor` (redact), `tithe_warden` (tithe_aura) and `harrier` (binding_geas).
        // §7a and this exit criterion contradict each other. This gate implements the
        // exit criterion; those three need promoting to elite, or the spec needs a
        // documented carve-out, before they can be authored.
        var axisAKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "tithe_aura", "redact", "school_grudge",
            "action_tax", "binding_geas", "overdraw_ward",
            "hand_cap",
        };
        // U3c: auras are STATES, not events (units doc §5). They never enter the
        // trigger stack, so they have no BuildTriggeredEffect case and are exempt from
        // that audit — they are cached on Unit by RecacheSelfAuras and read inline in
        // the damage path. An aura key MUST use trigger "aura", and a non-aura key must
        // NOT: a chitin authored as onStruck would silently vanish.
        var auraKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "chitin", "veil",      // U3c — self, cached by Unit.RecacheSelfAuras
            "bodyguard",           // U3d — radius, recomputed by ApplyEnemyAuras
            "tithe_aura",          // U3e — global, recomputed by ApplyEnemyAuras
            "action_tax",          // U3e — radius, read at StartPlayerTurn
            "binding_geas",        // U3e — read at Unit.OnMoved
            "school_grudge",       // U3e — read on the "AbilityCast" bus event
            "hand_cap",            // U3e — global, recomputed by ApplyEnemyAuras
        };
        // U3b: what the SCHEMA allows (units doc §5) — a superset of what runs.
        var knownTriggers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "onSpawn", "onDeath", "onAllyDeath", "onAttack", "onStruck", "onTurnEnd", "everyNRounds",
            "aura",   // U3c — continuous state, never queued
        };
        // U3b: triggers with a REAL enemy-side call site. This is the check that was
        // missing: before U3b only onDeath/onAllyDeath were wired, so authoring any
        // other trigger produced a SILENT NO-OP that nothing caught — the same trap
        // that nearly put shield_self/apply_bleed onto unit JSONs. Adding a name here
        // without its call site re-opens it.
        var wiredTriggers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "onDeath",      // QueueDeathTriggers
            "onAllyDeath",  // QueueDeathTriggers
            "onSpawn",      // FireEnemySpawnTriggers
            "onAttack",     // ResolveStrike
            "onStruck",     // Unit.OnStruck -> HandleUnitStruck
            "onTurnEnd",    // RunEnemyTurn
            "everyNRounds", // RunEnemyTurn
            "aura",         // U3c — Unit.RecacheSelfAuras + the ApplyDamage/MitigateCore path
        };
        foreach (var def in _cache.Values)
        {
            if (!knownKeys.Contains(def.BehaviorKey))
            {
                sb.AppendLine(cycleOnlyKeys.Contains(def.BehaviorKey)
                    ? $"  {def.Id}: '{def.BehaviorKey}' is CYCLE-ONLY — illegal as a BehaviorKey"
                    : $"  {def.Id}: UNKNOWN BehaviorKey '{def.BehaviorKey}'");
                ok = false;
            }
            // U3a: every scripted beat must name a real planner, and a script made
            // only of non-advancing beats would deadlock the unit.
            if (def.IntentCycle.Count > 0)
            {
                bool anyAdvancing = false;
                foreach (var beat in def.IntentCycle)
                {
                    if (knownKeys.Contains(beat))
                        anyAdvancing = true;
                    else if (!cycleOnlyKeys.Contains(beat))
                    {
                        sb.AppendLine($"  {def.Id}: UNKNOWN IntentCycle beat '{beat}'");
                        ok = false;
                    }
                }
                if (!anyAdvancing && def.CycleLoops)
                {
                    sb.AppendLine($"  {def.Id}: looping IntentCycle has no advancing beat — unit would never act");
                    ok = false;
                }
            }
            foreach (var tag in def.BehaviorTags)
            {
                if (!knownTags.Contains(tag))
                {
                    sb.AppendLine($"  {def.Id}: UNKNOWN BehaviorTag '{tag}'");
                    ok = false;
                }
            }
            if (def.Abilities.Count > 2)
            {
                sb.AppendLine($"  {def.Id}: {def.Abilities.Count} abilities (hard cap 2)");
                ok = false;
            }
            // V2: role must be in the §3 vocabulary (missing → "line" default).
            if (def.Role != "line" && def.Role != "elite" && def.Role != "boss" && def.Role != "summon")
            {
                sb.AppendLine($"  {def.Id}: UNKNOWN Role '{def.Role}'");
                ok = false;
            }
            foreach (var ab in def.Abilities)
            {
                if (!knownAbilityKeys.Contains(ab.Key))
                {
                    sb.AppendLine($"  {def.Id}: UNKNOWN ability Key '{ab.Key}'");
                    ok = false;
                }
                if (!knownTriggers.Contains(ab.Trigger))
                {
                    sb.AppendLine($"  {def.Id}: UNKNOWN ability Trigger '{ab.Trigger}'");
                    ok = false;
                }
                else if (!wiredTriggers.Contains(ab.Trigger))
                {
                    sb.AppendLine($"  {def.Id}: Trigger '{ab.Trigger}' has NO enemy call site — silent no-op");
                    ok = false;
                }
                // U3e: Axis A is elite-gated. A resource-denial key on a line unit is
                // a rejection, not a warning — the failure mode it guards against is
                // "every third encounter taxes your hand", which reads as the game
                // being broken rather than as a fight being hard.
                if (axisAKeys.Contains(ab.Key) && !def.IsElite && !def.IsBoss)
                {
                    sb.AppendLine($"  {def.Id}: Axis A key '{ab.Key}' on a '{def.Role}' unit — " +
                                  "resource denial is elite/boss only (spec §8, U3e)");
                    ok = false;
                }
                // U3e: school_grudge's school param must name a real CardSchool, or the
                // ability is a silent no-op for the whole fight. Checked at load rather
                // than discovered as "the elite never got stronger".
                if (string.Equals(ab.Key, "school_grudge", StringComparison.OrdinalIgnoreCase))
                {
                    string sc = ab.GetStringParam("school", "");
                    // "player" is the dynamic form (2026-07-28): resolved per cast against
                    // the casting unit's own school. Anything else must name a real
                    // CardSchool, or the ability is a silent no-op for the whole fight.
                    bool okSchool = string.Equals(sc, "player", StringComparison.OrdinalIgnoreCase)
                                    || Enum.TryParse<CardSchool>(sc, true, out _);
                    if (!okSchool)
                    {
                        sb.AppendLine($"  {def.Id}: school_grudge school '{sc}' is neither " +
                                      "a CardSchool nor \"player\"");
                        ok = false;
                    }
                }
                // U3c: aura keys and the "aura" trigger must agree in BOTH directions.
                bool keyIsAura = auraKeys.Contains(ab.Key);
                bool trigIsAura = string.Equals(ab.Trigger, "aura", StringComparison.OrdinalIgnoreCase);
                if (keyIsAura != trigIsAura)
                {
                    sb.AppendLine(keyIsAura
                        ? $"  {def.Id}: aura key '{ab.Key}' must use trigger 'aura', not '{ab.Trigger}'"
                        : $"  {def.Id}: '{ab.Key}' is not an aura but uses trigger 'aura' — would never fire");
                    ok = false;
                }
            }
        }

        // Round-trip a definition WITH tags + an ability through the loader's options.
        var probe = new UnitDefinition
        {
            Id = "rt_probe", ThreatLabel = "Probe", MaxHealth = 33, BaseSpeed = 2,
            Armor = 1, AttackRange = 2, AttackDamage = 6, PreferredDistance = 2,
            BehaviorKey = "melee_advance",
            BehaviorTags = new List<string> { "pack", "scout" },
            IntentCycle = new List<string> { "hold_ground", "melee_advance" },
            CycleLoops = false,
            Abilities = new List<UnitAbilityDef>
            {
                new() { Key = "requiem", Trigger = "onAllyDeath", Name = "Requiem",
                        Params = new Dictionary<string, string> { { "amount", "2" } } },
            },
            ColorR = 0.1f, ColorG = 0.6f, ColorB = 0.9f,
        };
        var rt = JsonSerializer.Deserialize<UnitDefinition>(
            JsonSerializer.Serialize(probe, JsonOptions), JsonOptions);
        bool rok = rt != null && rt.Id == probe.Id && rt.MaxHealth == probe.MaxHealth &&
                   rt.AttackDamage == probe.AttackDamage && rt.BehaviorKey == probe.BehaviorKey &&
                   rt.BehaviorTags.Count == 2 && rt.HasTag("pack") && rt.HasTag("scout") &&
                   rt.IntentCycle.Count == 2 && rt.IntentCycle[0] == "hold_ground" &&
                   rt.CycleLoops == false &&
                   rt.Abilities.Count == 1 && rt.Abilities[0].Key == "requiem" &&
                   rt.Abilities[0].Trigger == "onAllyDeath" &&
                   rt.Abilities[0].GetIntParam("amount", 0) == 2 &&
                   Mathf.IsEqualApprox(rt.ColorB, probe.ColorB);
        sb.AppendLine(rok ? "  UnitDefinition round-trip (incl. tags + abilities): OK" : "  UnitDefinition round-trip: FAIL");
        ok &= rok;

        // U1 JSONs have no behaviorTags key — must deserialize to empty list, not null.
        // U3a: and no intentCycle key — must deserialize empty (NOT null) and
        // CycleLoops must default TRUE, or every legacy unit changes behaviour.
        bool aok = Get("generic_soldier").BehaviorTags != null
                && Get("generic_soldier").IntentCycle != null
                && Get("generic_soldier").IntentCycle.Count == 0
                && Get("generic_soldier").CycleLoops;
        sb.AppendLine(aok ? "  Additive-schema (missing tags/cycle → empty): OK" : "  Additive-schema: FAIL");
        ok &= aok;

        // Legacy alias resolution (the loader's contract).
        bool lok = TryResolveId("Soldier", out var lid) && lid == "generic_soldier" &&
                   TryResolveId("generic_wizard", out var did) && did == "generic_wizard" &&
                   !TryResolveId("no_such_unit", out _);
        sb.AppendLine(lok ? "  Alias resolution (name→id, id→id, junk→fail): OK" : "  Alias resolution: FAIL");
        ok &= lok;

        sb.AppendLine(ok ? "RESULT: ALL PASSED" : "RESULT: FAILURES ABOVE");
        GD.Print(sb.ToString());
        if (!ok) GD.PushError("[UnitRegistry] Parity/round-trip assertion FAILED — see Output.");
        return ok;
    }
}
